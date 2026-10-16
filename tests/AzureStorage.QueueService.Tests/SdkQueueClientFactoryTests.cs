using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Azure.Storage.Queues.Specialized;
using FluentAssertions;

namespace AzureStorage.QueueService.Tests;

public class SdkQueueClientFactoryTests
{
    private const string Description = "Queue client 'test'";

    [Fact]
    public void CreateClient_WithEndpointAndCredential_ReturnsQueueClient()
    {
        var settings = new QueueClientSettings
        {
            TokenCredential = new DefaultAzureCredential(),
            EndpointUri = new Uri("https://fake.com/queue1"),
        };

        new SdkQueueClientFactory().CreateClient(Description, settings).Name.Should().Be("queue1");
    }

    [Fact]
    public void CreateClient_WithConnectionString_ReturnsQueueClient()
    {
        var settings = new QueueClientSettings
        {
            ConnectionString = "UseDevelopmentStorage=true",
            QueueName = "queue1",
        };

        new SdkQueueClientFactory().CreateClient(Description, settings).Name.Should().Be("queue1");
    }

    [Fact]
    public void CreateClient_WithoutUri_ThrowsNamingTheClient()
    {
        var settings = new QueueClientSettings
        {
            TokenCredential = new DefaultAzureCredential(),
            QueueName = "queue1",
        };

        new SdkQueueClientFactory().Invoking(x => x.CreateClient(Description, settings))
            .Should().Throw<InvalidOperationException>().WithMessage($"{Description}*");
    }

    [Fact]
    public void CreateClient_WithConnectionStringButNoQueueName_Throws()
    {
        var settings = new QueueClientSettings { ConnectionString = "UseDevelopmentStorage=true" };

        new SdkQueueClientFactory().Invoking(x => x.CreateClient(Description, settings))
            .Should().Throw<InvalidOperationException>().WithMessage("*QueueName*");
    }

    [Fact]
    public void CreateClient_WithNothingConfigured_Throws()
    {
        var settings = new QueueClientSettings { QueueName = "queue1" };

        new SdkQueueClientFactory().Invoking(x => x.CreateClient(Description, settings))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateClient_Base64_KeepsCallerPipelineAndHandlers_WithoutChangingCallerOptions()
    {
        // arrange: settings with no public getter (a policy, a transport, a handler) must still reach the library's
        // clients, and the library's own handler must stay off the caller's object.
        var transport = new PeekTransport("not base64!");
        var perCall = new CountingPolicy();
        var perRetry = new CountingPolicy();
        var callerHandled = 0;
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64, Transport = transport };
        options.AddPolicy(perCall, HttpPipelinePosition.PerCall);
        options.AddPolicy(perRetry, HttpPipelinePosition.PerRetry);
        options.MessageDecodingFailed += _ =>
        {
            callerHandled++;
            return Task.CompletedTask;
        };
        var source = new QueueClientSettings { ConnectionString = "UseDevelopmentStorage=true", QueueName = "orders", ClientOptions = options };
        var factory = new SdkQueueClientFactory();

        // act: the source client and the poison-queue client, which shares the source's ClientOptions
        QueueClient[] clients =
        [
            factory.CreateClient(Description, source),
            factory.CreateClient(Description, QueueClientFactory.ForSiblingQueue(source, "orders-poison")),
        ];

        // assert
        foreach (var client in clients)
        {
            var failures = DecodingFailures.Begin();
            try
            {
                (await client.PeekMessagesAsync(1)).Value.Should().BeEmpty();
            }
            finally
            {
                DecodingFailures.End();
            }

            failures.Should().ContainSingle("the library's handler is on the client it built");
        }

        (transport.Requests, perCall.Calls, perRetry.Calls, callerHandled).Should().Be((2, 2, 2, 2));

        // Outside a library receive the library's handler would throw; a direct client sees only the caller's handler.
        (await new QueueClient("UseDevelopmentStorage=true", "orders", options).PeekMessagesAsync(1)).Value.Should().BeEmpty();
        (perCall.Calls, callerHandled).Should().Be((3, 3), "the caller's options still hold exactly what the caller put there");
    }

    [Fact]
    public void Copy_CarriesEveryPublicSetting()
    {
        var encryption = new ClientSideEncryptionOptions(ClientSideEncryptionVersion.V2_0);
        var retryPolicy = new RetryPolicy(maxRetries: 9);
        var source = new SpecializedQueueClientOptions(QueueClientOptions.ServiceVersion.V2019_12_12)
        {
            ClientSideEncryption = encryption,
            MessageEncoding = QueueMessageEncoding.Base64,
            Audience = QueueAudience.CreateQueueServiceAccountAudience("account"),
            EnableTenantDiscovery = true,
            GeoRedundantSecondaryUri = new Uri("https://account-secondary.queue.core.windows.net"),
            RetryPolicy = retryPolicy,
        };
        source.Retry.Mode = RetryMode.Fixed;
        source.Retry.MaxRetries = 7;
        source.Retry.Delay = TimeSpan.FromSeconds(2);
        source.Retry.MaxDelay = TimeSpan.FromSeconds(20);
        source.Retry.NetworkTimeout = TimeSpan.FromSeconds(30);
        source.Diagnostics.IsLoggingEnabled = false;
        source.Diagnostics.IsDistributedTracingEnabled = false;
        source.Diagnostics.IsTelemetryEnabled = false;
        source.Diagnostics.IsLoggingContentEnabled = true;
        source.Diagnostics.LoggedContentSizeLimit = 99;
        source.Diagnostics.ApplicationId = "orders-app";
        source.Diagnostics.LoggedHeaderNames.Add("x-custom");
        source.Diagnostics.LoggedQueryParameters.Add("custom");

        var copy = QueueClientOptionsCopy.Copy(source);

        var specialized = copy.Should().BeOfType<SpecializedQueueClientOptions>().Subject;
        specialized.Should().NotBeSameAs(source);
        specialized.ClientSideEncryption.Should().BeSameAs(encryption);
        copy.Version.Should().Be(QueueClientOptions.ServiceVersion.V2019_12_12);
        copy.MessageEncoding.Should().Be(QueueMessageEncoding.Base64);
        copy.Audience.Should().Be(source.Audience);
        copy.EnableTenantDiscovery.Should().BeTrue();
        copy.GeoRedundantSecondaryUri.Should().Be(source.GeoRedundantSecondaryUri);
        copy.RetryPolicy.Should().BeSameAs(retryPolicy);
        copy.Retry.Should().BeEquivalentTo(source.Retry);
        copy.Diagnostics.Should().BeEquivalentTo(source.Diagnostics);
        copy.Diagnostics.LoggedHeaderNames.Should().NotBeSameAs(source.Diagnostics.LoggedHeaderNames);
    }

    [Fact]
    public void Copy_KnowsEverySettingOfTheInstalledAzureSdk()
    {
        // Fails on an Azure SDK update that renames a member the copy reads by reflection (the library would fall back
        // to briefly changing caller options) or adds a setting the copy doesn't carry yet (it would be dropped).
        QueueClientOptionsCopy.IsSupported.Should().BeTrue();

        static IEnumerable<string> Members([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicEvents)] Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
                .Concat(type.GetEvents(BindingFlags.Public | BindingFlags.Instance).Select(e => e.Name));

        Members(typeof(SpecializedQueueClientOptions)).Should().BeEquivalentTo(
            "ClientSideEncryption", "Version", "GeoRedundantSecondaryUri", "MessageEncoding", "EnableTenantDiscovery",
            "Audience", "Transport", "Diagnostics", "Retry", "RetryPolicy", "MessageDecodingFailed");
        Members(typeof(RetryOptions)).Should().BeEquivalentTo("MaxRetries", "Delay", "MaxDelay", "Mode", "NetworkTimeout");
        Members(typeof(DiagnosticsOptions)).Should().BeEquivalentTo(
            "IsLoggingEnabled", "IsDistributedTracingEnabled", "IsTelemetryEnabled", "IsLoggingContentEnabled",
            "LoggedContentSizeLimit", "LoggedHeaderNames", "LoggedQueryParameters", "ApplicationId");
    }

    private sealed class CountingPolicy : HttpPipelineSynchronousPolicy
    {
        public int Calls;

        public override void OnSendingRequest(HttpMessage message) => Calls++;
    }

    /// <summary>Answers every request with a peek result holding one message whose text is <paramref name="messageText"/>.</summary>
    private sealed class PeekTransport(string messageText) : HttpPipelineTransport
    {
        private readonly HttpClientTransport _requests = new();
        public int Requests;

        public override Request CreateRequest() => _requests.CreateRequest();

        public override void Process(HttpMessage message) => ProcessAsync(message).AsTask().GetAwaiter().GetResult();

        public override ValueTask ProcessAsync(HttpMessage message)
        {
            Requests++;
            message.Response = new XmlResponse(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><QueueMessagesList><QueueMessage><MessageId>bad</MessageId>" +
                "<InsertionTime>Mon, 01 Jan 2024 00:00:00 GMT</InsertionTime><ExpirationTime>Mon, 08 Jan 2024 00:00:00 GMT</ExpirationTime>" +
                $"<DequeueCount>1</DequeueCount><MessageText>{messageText}</MessageText></QueueMessage></QueueMessagesList>");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class XmlResponse(string body) : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; } = new MemoryStream(Encoding.UTF8.GetBytes(body));
        public override string ClientRequestId { get; set; } = string.Empty;
        public override void Dispose() { }

        protected override bool ContainsHeader(string name) => TryGetHeader(name, out _);
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [new HttpHeader("Content-Type", "application/xml")];

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ? "application/xml" : null;
            return value is not null;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = TryGetHeader(name, out var value) ? [value] : null;
            return values is not null;
        }
    }
}
