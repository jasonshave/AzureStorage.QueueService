using System.Diagnostics;
using AzureStorage.QueueService.Telemetry;
using FluentAssertions;

namespace AzureStorage.QueueService.Tests;

public class TraceContextEnvelopeTests
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact(DisplayName = "A custom serializer's non-JSON output travels Base64-encoded and comes back byte for byte")]
    public void Non_Json_Payload_Round_Trips()
    {
        byte[] bytes = [0xFF, 0x00, (byte)'{', 0x80];
        using var caller = new Activity("caller").Start();

        var wire = TraceContextEnvelope.Wrap(new BinaryData(bytes), caller)!;

        wire.ToString().Should().Contain("\"body64\":");
        TraceContextEnvelope.TryUnwrap(wire, out var payload, out var context).Should().BeTrue();
        payload.ToArray().Should().Equal(bytes);
        context!.Value.TraceId.Should().Be(caller.TraceId);
    }

    [Theory(DisplayName = "A payload that only resembles an envelope is left alone")]
    [InlineData("{\"traceparent\":\"" + TraceParent + "\",\"body\":{}}")]                          // no marker
    [InlineData("{\"body\":{},\"$azqs-envelope\":1,\"traceparent\":\"" + TraceParent + "\"}")]     // marker not first
    [InlineData("{\"$azqs-envelope\":2,\"traceparent\":\"" + TraceParent + "\",\"body\":{}}")]     // a version this reader does not know
    [InlineData("{\"$azqs-envelope\":1,\"traceparent\":\"" + TraceParent + "\",\"body\":{},\"orderId\":7}")] // a property the format does not have
    [InlineData("{\"$azqs-envelope\":1,\"traceparent\":\"" + TraceParent + "\",\"body\":{}")]      // truncated
    [InlineData("{\"$azqs-envelope\":1,\"traceparent\":\"" + TraceParent + "\",\"body64\":\"not base64!\"}")] // body64 that is not Base64
    public void Lookalikes_Are_Not_Unwrapped(string text)
    {
        TraceContextEnvelope.TryUnwrap(BinaryData.FromString(text), out var payload, out var context).Should().BeFalse();

        payload.ToString().Should().Be(text);
        context.Should().BeNull();
    }

    [Fact(DisplayName = "A lookalike with a string that is not valid UTF-8 is left alone rather than failing the receive")]
    public void Lookalike_With_Invalid_Utf8_Is_Not_Unwrapped()
    {
        byte[] bytes = [.. "{\"$azqs-envelope\":1,\"traceparent\":\""u8, 0xFF, 0xFE, .. "\",\"body\":{}}"u8];

        TraceContextEnvelope.TryUnwrap(new BinaryData(bytes), out var payload, out var context).Should().BeFalse();

        payload.ToArray().Should().Equal(bytes);
        context.Should().BeNull();
    }
}
