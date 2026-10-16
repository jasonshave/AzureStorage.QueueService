using System.Buffers;
using System.Diagnostics;
using System.Text.Json;

namespace AzureStorage.QueueService.Telemetry;

/// <summary>
/// Carries the sender's W3C trace context inside a message body, because a storage queue message has nothing else to
/// carry it in: no properties, no headers. Clients with <see cref="QueueClientSettings.PropagateTraceContext"/> on wrap
/// what they send; every client unwraps what it receives.
/// </summary>
/// <remarks>
/// Wire format, version 1 — a JSON object whose first property is the marker:
/// <code>
/// {"$azqs-envelope":1,"traceparent":"00-&lt;trace id&gt;-&lt;span id&gt;-&lt;flags&gt;","tracestate":"...","body":&lt;the serialized message&gt;}
/// </code>
/// <c>tracestate</c> is present only when there is one. <c>body</c> holds the serializer's output verbatim when it is
/// JSON (the default serializer always is); a custom serializer's non-JSON output goes in <c>body64</c> as Base64
/// instead. The SDK's message encoding (for example Base64) applies to the whole envelope, as it did to the plain body.
/// Detection is strict so an ordinary payload is never mistaken for an envelope: the marker must come first with a
/// known version, and only the properties above may follow. The marker's hyphen means no C# property name can
/// produce it by accident.
/// </remarks>
internal static class TraceContextEnvelope
{
    public const int Version = 1;

    private static ReadOnlySpan<byte> MarkerUtf8 => "$azqs-envelope"u8;
    private static ReadOnlySpan<byte> TraceParentUtf8 => "traceparent"u8;
    private static ReadOnlySpan<byte> TraceStateUtf8 => "tracestate"u8;
    private static ReadOnlySpan<byte> BodyUtf8 => "body"u8;
    private static ReadOnlySpan<byte> Body64Utf8 => "body64"u8;

    // The writer nests the payload one level inside the envelope, so reading allows deeper than the check on write
    // (64, System.Text.Json's default); an envelope this library wrote can never fail to unwrap on depth.
    private static readonly JsonReaderOptions ReadOptions = new() { MaxDepth = 256 };

    /// <summary>
    /// Wraps <paramref name="payload"/> with the context of <paramref name="activity"/>, or returns null when the
    /// activity has no W3C identity to carry (the legacy hierarchical id format), in which case the message goes out plain.
    /// </summary>
    public static BinaryData? Wrap(BinaryData payload, Activity activity)
    {
        if (activity.IdFormat != ActivityIdFormat.W3C) return null;

        var context = activity.Context;
        var traceParent = $"00-{context.TraceId.ToHexString()}-{context.SpanId.ToHexString()}-{(byte)context.TraceFlags:x2}";
        var body = payload.ToMemory().Span;

        var buffer = new ArrayBufferWriter<byte>(body.Length + 160);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(MarkerUtf8, Version);
            writer.WriteString(TraceParentUtf8, traceParent);
            if (!string.IsNullOrEmpty(activity.TraceStateString)) writer.WriteString(TraceStateUtf8, activity.TraceStateString);
            if (IsSingleJsonValue(body))
            {
                writer.WritePropertyName(BodyUtf8);
                writer.WriteRawValue(body, skipInputValidation: true);
            }
            else
            {
                writer.WriteBase64String(Body64Utf8, body);
            }
            writer.WriteEndObject();
        }

        return new BinaryData(buffer.WrittenMemory);
    }

    /// <summary>
    /// When <paramref name="wire"/> is an envelope, returns the original payload (a slice of the same bytes for a JSON
    /// body) and the sender's context, if it parses. Anything else is a plain message and is left alone.
    /// </summary>
    public static bool TryUnwrap(BinaryData wire, out BinaryData payload, out ActivityContext? creationContext)
    {
        payload = wire;
        creationContext = null;

        var memory = wire.ToMemory();
        if (!StartsWithObject(memory.Span)) return false; // the cheap test first: most plain payloads stop here

        try
        {
            return TryRead(memory, ref payload, ref creationContext);
        }
        catch (JsonException)
        {
            // Starts like JSON but is not: a plain message.
            payload = wire;
            creationContext = null;
            return false;
        }
    }

    private static bool TryRead(ReadOnlyMemory<byte> memory, ref BinaryData payload, ref ActivityContext? creationContext)
    {
        var reader = new Utf8JsonReader(memory.Span, ReadOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
        if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(MarkerUtf8)) return false;
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version) || version != Version) return false;

        string? traceParent = null, traceState = null;
        BinaryData? body = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(TraceParentUtf8) && traceParent is null)
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                traceParent = reader.GetString();
            }
            else if (reader.ValueTextEquals(TraceStateUtf8) && traceState is null)
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                traceState = reader.GetString();
            }
            else if (reader.ValueTextEquals(BodyUtf8) && body is null)
            {
                if (!reader.Read()) return false;
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                body = new BinaryData(memory[start..(int)reader.BytesConsumed]);
            }
            else if (reader.ValueTextEquals(Body64Utf8) && body is null)
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                body = new BinaryData(reader.GetBytesFromBase64());
            }
            else
            {
                return false; // an unknown or repeated property: not ours
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || traceParent is null || body is null) return false;
        if (reader.Read()) return false; // something after the object

        payload = body;
        // A malformed traceparent still unwraps: the body is intact, there is just no context to continue.
        creationContext = ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var context) ? context : null;
        return true;
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
