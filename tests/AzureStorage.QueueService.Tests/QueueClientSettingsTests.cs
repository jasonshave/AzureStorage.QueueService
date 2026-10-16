using Azure.Identity;
using FluentAssertions;

namespace AzureStorage.QueueService.Tests;

/// <summary>
/// Validation by responsibility: connection checks apply to every SDK client the library builds; source-queue
/// policy (poison destination, receive timing) applies to source queues only. Uses the real SDK factory where the
/// reviewer's regressions showed a mock would hide the problem.
/// </summary>
public class QueueClientSettingsTests
{
    private const string ConnectionString = "UseDevelopmentStorage=true";
    private const string Account = "https://account.queue.core.windows.net/";

    // ---------------------------------------------------------------------------------------------------------
    // S1: a poison destination is a destination, not a source with a policy of its own
    // ---------------------------------------------------------------------------------------------------------

    [Theory(DisplayName = "S1: the poison client can be built for every default-named source the source validation accepts")]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(56)]
    public void Poison_Client_Builds_For_Default_Names(int sourceLength)
    {
        var source = new QueueClientSettings { ConnectionString = ConnectionString, QueueName = new string('a', sourceLength) };
        source.Validate().Should().BeNull();

        var poison = new SdkQueueClientFactory().CreateClient("poison", QueueClientFactory.ForSiblingQueue(source, source.EffectivePoisonQueueName!));

        poison.Name.Should().HaveLength(sourceLength + "-poison".Length);
    }

    [Theory(DisplayName = "S1: an explicit poison name up to the 63-character limit builds a real poison client")]
    [InlineData(57)]
    [InlineData(63)]
    public void Poison_Client_Builds_For_Long_Explicit_Names(int poisonLength)
    {
        var source = new QueueClientSettings { ConnectionString = ConnectionString, QueueName = "orders", PoisonQueueName = new string('p', poisonLength) };
        source.Validate().Should().BeNull();

        var poison = new SdkQueueClientFactory().CreateClient("poison", QueueClientFactory.ForSiblingQueue(source, source.EffectivePoisonQueueName!));

        poison.Name.Should().HaveLength(poisonLength);
    }

    [Fact(DisplayName = "S1: the same holds for endpoint authentication")]
    public void Poison_Client_Builds_For_Endpoint_Registration()
    {
        var source = new QueueClientSettings { EndpointUri = new Uri(Account + new string('a', 56)), TokenCredential = new DefaultAzureCredential() };
        source.Validate().Should().BeNull();

        var poison = new SdkQueueClientFactory().CreateClient("poison", QueueClientFactory.ForSiblingQueue(source, source.EffectivePoisonQueueName!));

        poison.Name.Should().HaveLength(63);
    }

    // ---------------------------------------------------------------------------------------------------------
    // S2: the effective queue follows the same precedence as SDK construction
    // ---------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "S2: with endpoint authentication the queue comes from the URI, and a conflicting QueueName is rejected")]
    public void Endpoint_With_Conflicting_QueueName_Is_Rejected()
    {
        var settings = new QueueClientSettings
        {
            QueueName = "configured-alias",
            EndpointUri = new Uri(Account + "orders"),
            TokenCredential = new DefaultAzureCredential(),
            PoisonQueueName = "orders", // the self-poison loop the alias would have hidden
        };

        settings.EffectiveQueueName.Should().Be("orders");
        settings.ValidateConnection().Should().Contain("points at queue 'orders'");
        settings.Validate().Should().Contain("points at queue 'orders'");
    }

    [Fact(DisplayName = "S2: with endpoint authentication an agreeing QueueName is allowed")]
    public void Endpoint_With_Agreeing_QueueName_Is_Allowed()
    {
        var settings = new QueueClientSettings { QueueName = "orders", EndpointUri = new Uri(Account + "orders/"), TokenCredential = new DefaultAzureCredential() };

        settings.Validate().Should().BeNull();
        settings.EffectivePoisonQueueName.Should().Be("orders-poison");
    }

    [Fact(DisplayName = "An empty PoisonQueueName (a blank placeholder in configuration) falls back to the default")]
    public void Empty_PoisonQueueName_Falls_Back_To_Default()
    {
        var settings = new QueueClientSettings { ConnectionString = ConnectionString, QueueName = "orders", PoisonQueueName = "" };

        settings.Validate().Should().BeNull();
        settings.EffectivePoisonQueueName.Should().Be("orders-poison");
    }

    [Fact(DisplayName = "S2: with a connection string the queue is QueueName, and an EndpointUri left over is ignored")]
    public void ConnectionString_Takes_Precedence_Over_Endpoint()
    {
        var settings = new QueueClientSettings { ConnectionString = ConnectionString, QueueName = "orders", EndpointUri = new Uri(Account + "other") };

        settings.EffectiveQueueName.Should().Be("orders");
        settings.Validate().Should().BeNull();
    }

    [Fact(DisplayName = "S2: an endpoint whose path has no queue is rejected")]
    public void Endpoint_Without_Queue_Is_Rejected()
    {
        var settings = new QueueClientSettings { EndpointUri = new Uri(Account), TokenCredential = new DefaultAzureCredential() };

        settings.ValidateConnection().Should().Contain("no queue name");
    }

    // ---------------------------------------------------------------------------------------------------------
    // S3: receive timing
    // ---------------------------------------------------------------------------------------------------------

    [Theory(DisplayName = "S3: a client VisibilityTimeout outside 1 second to 7 days is rejected; the boundaries are accepted")]
    [InlineData(0.5, false)]
    [InlineData(1, true)]
    [InlineData(7 * 24 * 3600, true)]
    [InlineData(7 * 24 * 3600 + 1, false)]
    public void Client_VisibilityTimeout_Is_Bounded(double seconds, bool valid)
    {
        var settings = new QueueClientSettings { ConnectionString = ConnectionString, QueueName = "orders", VisibilityTimeout = TimeSpan.FromSeconds(seconds) };

        (settings.Validate() is null).Should().Be(valid);
    }
}
