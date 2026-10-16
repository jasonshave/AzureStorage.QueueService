using System.Buffers;
using System.Diagnostics;
using System.Text.Json;

namespace AzureStorage.QueueService;

/// <summary>What <see cref="MessageEnvelope.TryRead"/> found on the queue.</summary>
internal enum EnvelopeKind
{
    /// <summary>An ordinary message: the body is the serializer's output as it is.</summary>
    Plain,

    /// <summary>An envelope this version can read.</summary>
    Envelope,

    /// <summary>
    /// An envelope this version cannot read: written by a newer version of the library, or damaged. The message is
    /// handed over with <see cref="QueueMessage{T}.Error"/> set so it is dead-lettered intact, and can be replayed once
    /// the reader is upgraded, rather than being read as something it is not.
    /// </summary>
    Unreadable,
}

/// <summary>Why and from where a message was dead-lettered; kept in the envelope on the poison queue.</summary>
internal sealed record DeadLetterInfo(string? Reason, string? Category, string? MessageId, long DequeueCount, string? Queue, DateTimeOffset? At);

/// <summary>
/// The library's wrapper around a message body. Storage queue messages have no properties or headers, so anything
/// that has to travel with a message (the sender's trace context, application headers, how the body is encoded,
/// where a large body was put, why a message was dead-lettered) travels in the body, inside this JSON object.
/// </summary>
/// <remarks>
/// <para>Wire format, version 1. The marker comes first; every other property is optional, in any order:</para>
/// <code>
/// {"$azqs-envelope":1,
///  "traceparent":"00-&lt;trace id&gt;-&lt;span id&gt;-&lt;flags&gt;", "tracestate":"...",
///  "headers":{"name":"value"},
///  "encoding":"gzip",
///  "deadLetter":{"reason":"...","category":"handler","messageId":"...","dequeueCount":5,"queue":"orders","at":"2026-10-16T12:00:00+00:00"},
///  "body":&lt;the serialized message&gt;   or   "body64":"&lt;Base64&gt;"   or   "blob":"&lt;where the body is stored&gt;"}
/// </code>
/// <para>
/// Exactly one of <c>body</c> (the serializer's output verbatim, when it is JSON), <c>body64</c> (any other bytes,
/// Base64-encoded) and <c>blob</c> (a reference to a body kept outside the queue) is present. <c>encoding</c> applies to
/// the bytes of <c>body64</c> or the blob, never to <c>body</c>.
/// </para>
/// <para>
/// Compatibility rules, so that a message written by a later 3.x is never silently misread by an earlier one: a
/// property this reader does not know is skipped, so later versions can add optional properties without changing the
/// version; anything that changes how the body must be read (a new encoding, a new kind of body) either uses a value
/// this reader rejects or increments the version. A version this reader does not know, or an envelope that breaks the
/// rules above, is <see cref="EnvelopeKind.Unreadable"/>: the message gets an error and is dead-lettered unchanged.
/// Detection needs the marker to be the first property; its hyphen means no C# property name can produce it by accident.
/// </para>
/// </remarks>
internal sealed class MessageEnvelope
{
    public const int Version = 1;
    public const string GzipEncoding = "gzip";

    private static ReadOnlySpan<byte> MarkerUtf8 => "$azqs-envelope"u8;
    private static ReadOnlySpan<byte> TraceParentUtf8 => "traceparent"u8;
    private static ReadOnlySpan<byte> TraceStateUtf8 => "tracestate"u8;
    private static ReadOnlySpan<byte> HeadersUtf8 => "headers"u8;
    private static ReadOnlySpan<byte> EncodingUtf8 => "encoding"u8;
    private static ReadOnlySpan<byte> DeadLetterUtf8 => "deadLetter"u8;
    private static ReadOnlySpan<byte> BodyUtf8 => "body"u8;
    private static ReadOnlySpan<byte> Body64Utf8 => "body64"u8;
    private static ReadOnlySpan<byte> BlobUtf8 => "blob"u8;

    // The writer nests the payload one level inside the envelope, so reading allows deeper than the check on write
    // (64, System.Text.Json's default); an envelope this library wrote can never fail to unwrap on depth.
    private static readonly JsonReaderOptions ReadOptions = new() { MaxDepth = 256 };

    public string? TraceParent { get; set; }

    public string? TraceState { get; set; }

    public IReadOnlyDictionary<string, string>? Headers { get; set; }

    /// <summary>How the bytes of <see cref="Body"/> (when not JSON) or the blob are encoded; null for as-is.</summary>
    public string? Encoding { get; set; }

    public DeadLetterInfo? DeadLetter { get; set; }

    /// <summary>The body: JSON when <see cref="BodyIsJson"/>, otherwise bytes. Null when the body is in a blob.</summary>
    public BinaryData? Body { get; set; }

    public bool BodyIsJson { get; set; }

    /// <summary>Where the body is stored when it is not on the queue; see <see cref="ILargeMessageStore"/>.</summary>
    public string? BlobReference { get; set; }

    /// <summary>The sender's trace context, when there is one and it parses.</summary>
    public ActivityContext? CreationContext =>
        TraceParent is not null && ActivityContext.TryParse(TraceParent, TraceState, isRemote: true, out var context) ? context : null;

    /// <summary>
    /// True when nothing but a body is left: the message can go on the queue as the body alone, the way it would have
    /// been sent with no features on.
    /// </summary>
    public bool CarriesOnlyBody =>
        TraceParent is null && Headers is not { Count: > 0 } && Encoding is null && DeadLetter is null && BlobReference is null && Body is not null;

    public MessageEnvelope Clone() => (MessageEnvelope)MemberwiseClone();

    /// <summary>
    /// Sets <see cref="TraceParent"/> and <see cref="TraceState"/> from <paramref name="activity"/>; returns false when it
    /// has no W3C identity to carry (the legacy hierarchical id format).
    /// </summary>
    public bool SetTraceContext(Activity activity)
    {
        if (activity.IdFormat != ActivityIdFormat.W3C) return false;

        var context = activity.Context;
        TraceParent = $"00-{context.TraceId.ToHexString()}-{context.SpanId.ToHexString()}-{(byte)context.TraceFlags:x2}";
        TraceState = string.IsNullOrEmpty(activity.TraceStateString) ? null : activity.TraceStateString;
        return true;
    }

    /// <summary>Sets the body, as JSON when it is exactly one JSON value (or the caller already knows it is), otherwise as bytes.</summary>
    public void SetBody(BinaryData body, bool knownJson = false)
    {
        Body = body;
        BodyIsJson = knownJson || IsSingleJsonValue(body.ToMemory().Span);
        BlobReference = null;
    }

    public BinaryData Write()
    {
        var body = Body?.ToMemory() ?? ReadOnlyMemory<byte>.Empty;
        var buffer = new ArrayBufferWriter<byte>(body.Length + 256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(MarkerUtf8, Version);
            if (TraceParent is not null) writer.WriteString(TraceParentUtf8, TraceParent);
            if (TraceState is not null) writer.WriteString(TraceStateUtf8, TraceState);

            if (Headers is { Count: > 0 } headers)
            {
                writer.WriteStartObject(HeadersUtf8);
                foreach (var (name, value) in headers) writer.WriteString(name, value);
                writer.WriteEndObject();
            }

            if (Encoding is not null) writer.WriteString(EncodingUtf8, Encoding);

            if (DeadLetter is { } deadLetter)
            {
                writer.WriteStartObject(DeadLetterUtf8);
                if (deadLetter.Reason is not null) writer.WriteString("reason"u8, deadLetter.Reason);
                if (deadLetter.Category is not null) writer.WriteString("category"u8, deadLetter.Category);
                if (deadLetter.MessageId is not null) writer.WriteString("messageId"u8, deadLetter.MessageId);
                writer.WriteNumber("dequeueCount"u8, deadLetter.DequeueCount);
                if (deadLetter.Queue is not null) writer.WriteString("queue"u8, deadLetter.Queue);
                if (deadLetter.At is { } at) writer.WriteString("at"u8, at);
                writer.WriteEndObject();
            }

            if (BlobReference is not null)
            {
                writer.WriteString(BlobUtf8, BlobReference);
            }
            else if (BodyIsJson)
            {
                writer.WritePropertyName(BodyUtf8);
                writer.WriteRawValue(body.Span, skipInputValidation: true);
            }
            else
            {
                writer.WriteBase64String(Body64Utf8, body.Span);
            }

            writer.WriteEndObject();
        }

        return new BinaryData(buffer.WrittenMemory);
    }

    /// <summary>
    /// Looks at a message as it came off the queue. Never throws: it runs inside every receive, so one malformed message
    /// must not fail the batch it arrived in. <paramref name="problem"/> says what is wrong with an unreadable envelope.
    /// </summary>
    public static EnvelopeKind TryRead(BinaryData wire, out MessageEnvelope? envelope, out string? problem)
    {
        envelope = null;
        problem = null;

        var memory = wire.ToMemory();
        if (!StartsWithObject(memory.Span)) return EnvelopeKind.Plain; // the cheap test first: most plain payloads stop here

        var reader = new Utf8JsonReader(memory.Span, ReadOptions);
        try
        {
            // Up to and including the marker, anything unexpected means an ordinary message that happens to start with '{'.
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return EnvelopeKind.Plain;
            if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(MarkerUtf8)) return EnvelopeKind.Plain;
        }
        catch (JsonException)
        {
            return EnvelopeKind.Plain;
        }

        // From here on it is ours, so anything wrong is an unreadable envelope, never a plain message: read as a plain
        // message, the envelope's JSON would deserialize into an object with empty fields and be completed.
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version) || version < 1)
                return Unreadable("its version marker is not a positive whole number", out problem);
            if (version > Version)
                return Unreadable($"it was written in envelope version {version} by a newer version of AzureStorage.QueueService, and this one reads version {Version}. Upgrade the application, then replay the message from the poison queue", out problem);

            var result = new MessageEnvelope();
            if (!TryReadProperties(ref reader, memory, result, out problem)) return EnvelopeKind.Unreadable;

            envelope = result;
            return EnvelopeKind.Envelope;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // Invalid JSON (JsonException), a string that is not valid UTF-8 or a value of the wrong type
            // (InvalidOperationException), or a malformed date (FormatException).
            return Unreadable($"it is not valid: {e.Message}", out problem);
        }
    }

    private static EnvelopeKind Unreadable(string why, out string? problem)
    {
        problem = why;
        return EnvelopeKind.Unreadable;
    }

    private static bool TryReadProperties(ref Utf8JsonReader reader, ReadOnlyMemory<byte> memory, MessageEnvelope result, out string? problem)
    {
        problem = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bodies = 0;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            if (!seen.Add(name)) return Fail($"it has the property '{name}' twice", out problem);

            if (reader.ValueTextEquals(TraceParentUtf8)) result.TraceParent = ReadString(ref reader, name);
            else if (reader.ValueTextEquals(TraceStateUtf8)) result.TraceState = ReadString(ref reader, name);
            else if (reader.ValueTextEquals(HeadersUtf8)) result.Headers = ReadHeaders(ref reader);
            else if (reader.ValueTextEquals(EncodingUtf8)) result.Encoding = ReadString(ref reader, name);
            else if (reader.ValueTextEquals(DeadLetterUtf8)) result.DeadLetter = ReadDeadLetter(ref reader);
            else if (reader.ValueTextEquals(BodyUtf8))
            {
                bodies++;
                reader.Read();
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                result.Body = new BinaryData(memory[start..(int)reader.BytesConsumed]);
                result.BodyIsJson = true;
            }
            else if (reader.ValueTextEquals(Body64Utf8))
            {
                bodies++;
                reader.Read();
                if (reader.TokenType != JsonTokenType.String || !reader.TryGetBytesFromBase64(out var bytes)) return Fail("its body64 is not a Base64 string", out problem);
                result.Body = new BinaryData(bytes);
            }
            else if (reader.ValueTextEquals(BlobUtf8))
            {
                bodies++;
                result.BlobReference = ReadString(ref reader, name);
                if (string.IsNullOrEmpty(result.BlobReference)) return Fail("its blob reference is empty", out problem);
            }
            else
            {
                // Added by a later version as optional: skip it.
                reader.Read();
                reader.Skip();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject) return Fail("it is not a complete JSON object", out problem);
        if (reader.Read()) return Fail("something follows the JSON object", out problem);
        if (bodies != 1) return Fail(bodies == 0 ? "it has no body" : "it has more than one body", out problem);

        if (result.Encoding is { } encoding)
        {
            if (result.BodyIsJson) return Fail("it has an encoding but a JSON body", out problem);
            if (encoding != GzipEncoding) return Fail($"its body is encoded as '{encoding}', which this version cannot decode. Upgrade the application, then replay the message from the poison queue", out problem);
        }

        return true;
    }

    private static bool Fail(string why, out string? problem)
    {
        problem = why;
        return false;
    }

    private static string? ReadString(ref Utf8JsonReader reader, string name)
    {
        reader.Read();
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new InvalidOperationException($"'{name}' must be a string.");
        return reader.GetString();
    }

    private static Dictionary<string, string> ReadHeaders(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidOperationException("'headers' must be an object.");

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            headers[name] = ReadString(ref reader, name) ?? throw new InvalidOperationException($"Header '{name}' must be a string.");
        }

        return headers;
    }

    private static DeadLetterInfo ReadDeadLetter(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidOperationException("'deadLetter' must be an object.");

        string? reason = null, category = null, messageId = null, queue = null;
        long dequeueCount = 0;
        DateTimeOffset? at = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("reason"u8)) reason = ReadString(ref reader, "reason");
            else if (reader.ValueTextEquals("category"u8)) category = ReadString(ref reader, "category");
            else if (reader.ValueTextEquals("messageId"u8)) messageId = ReadString(ref reader, "messageId");
            else if (reader.ValueTextEquals("queue"u8)) queue = ReadString(ref reader, "queue");
            else if (reader.ValueTextEquals("dequeueCount"u8)) { reader.Read(); dequeueCount = reader.GetInt64(); }
            else if (reader.ValueTextEquals("at"u8)) { reader.Read(); at = reader.GetDateTimeOffset(); }
            else { reader.Read(); reader.Skip(); }
        }

        return new DeadLetterInfo(reason, category, messageId, dequeueCount, queue, at);
    }

    private static bool StartsWithObject(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b == (byte)'{';
        }
        return false;
    }

    private static bool IsSingleJsonValue(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read()) return false;
            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
