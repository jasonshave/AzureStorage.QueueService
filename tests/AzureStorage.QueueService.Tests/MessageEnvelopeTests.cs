using System.Diagnostics;
using FluentAssertions;

namespace AzureStorage.QueueService.Tests;

/// <summary>
/// The wire format's compatibility rules: what counts as an envelope, what a later version may add without breaking
/// this one, and what this version must refuse rather than misread.
/// </summary>
public class MessageEnvelopeTests
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Theory(DisplayName = "Envelope detection: plain payloads are left alone, later optional properties are skipped, anything else ours but unreadable is refused")]
    [InlineData("{\"orderId\":7}", "Plain")]
    [InlineData("not json at all", "Plain")]
    [InlineData("{\"body\":{},\"$azqs-envelope\":1}", "Plain")]                                   // marker not first
    [InlineData("{\"$azqs-envelope\":1,\"body\":{\"a\":1}}", "Envelope")]                         // no trace context
    [InlineData("{\"$azqs-envelope\":1,\"traceparent\":\"" + TraceParent + "\",\"priority\":3,\"body\":{}}", "Envelope")] // a property from a later version
    [InlineData("{\"$azqs-envelope\":2,\"body\":{}}", "Unreadable")]                              // a later version
    [InlineData("{\"$azqs-envelope\":1,\"encoding\":\"br\",\"body64\":\"AA==\"}", "Unreadable")]   // an encoding this version cannot decode
    [InlineData("{\"$azqs-envelope\":1,\"traceparent\":\"x\"}", "Unreadable")]                     // no body
    [InlineData("{\"$azqs-envelope\":1,\"body\":{},\"body64\":\"AA==\"}", "Unreadable")]           // two bodies
    [InlineData("{\"$azqs-envelope\":1,\"body64\":\"not base64!\"}", "Unreadable")]
    [InlineData("{\"$azqs-envelope\":1,\"body\":{}", "Unreadable")]                                // truncated
    public void Detection(string text, string kind)
    {
        var expected = Enum.Parse<EnvelopeKind>(kind);
        MessageEnvelope.TryRead(BinaryData.FromString(text), out _, out var problem).Should().Be(expected);
        if (expected == EnvelopeKind.Unreadable) problem.Should().NotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Everything an envelope carries survives a write and a read: trace context, headers, encoding, dead-letter record and binary body")]
    public void Round_Trip()
    {
        using var caller = new Activity("caller").Start();
        byte[] bytes = [0xFF, 0x00, (byte)'{', 0x80];
        var written = new MessageEnvelope
        {
            Headers = new Dictionary<string, string> { ["tenant"] = "contoso" },
            Encoding = MessageEnvelope.GzipEncoding,
            DeadLetter = new DeadLetterInfo("gave up", DeadLetterCategories.Handler, "m1", 5, "orders", new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)),
        };
        written.SetTraceContext(caller);
        written.SetBody(new BinaryData(bytes));

        MessageEnvelope.TryRead(written.Write(), out var read, out _).Should().Be(EnvelopeKind.Envelope);

        read!.Body!.ToArray().Should().Equal(bytes);
        read.BodyIsJson.Should().BeFalse();
        read.Headers.Should().Equal(written.Headers);
        read.Encoding.Should().Be(MessageEnvelope.GzipEncoding);
        read.DeadLetter.Should().Be(written.DeadLetter);
        read.CreationContext!.Value.TraceId.Should().Be(caller.TraceId);
    }
}
