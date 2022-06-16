using FluentAssertions;
using JasonShave.AzureStorage.QueueService.Models;
using JasonShave.AzureStorage.QueueService.Services;

namespace AzureStorage.QueueService.Tests
{
    public class QueueClientFactoryTests
    {
        private readonly QueueClientSettings _queueClientSettings;
        
        public QueueClientFactoryTests()
        {
            _queueClientSettings = new QueueClientSettings()
            {
                QueueName = "Test",
                ConnectionString = "DefaultEndpointsProtocol=https;AccountName=test;AccountKey=FAKE/xd3SoV4C52caAxURkg7Pso+X5QyFprgcAeDCw7joUYCGx3J7B+V+PZ6znEQ0lN/Mvxqdkwi+AStHyBWuA==;EndpointSuffix=core.windows.net"
            };
        }

        [Fact(DisplayName = "Register queue client succeeds.")]
        public void Register_QueueClient()
        {
            // arrange
            var subject = new AzureQueueClientFactory();

            // act
            var result = subject.RegisterQueueClient(_queueClientSettings);

            // assert
            result.Should().Be(true);
        }

        [Fact(DisplayName = "Get queue client succeeds.")]
        public void Get_QueueClient()
        {
            // arrange
            var subject = new AzureQueueClientFactory();

            // act
            var result = subject.RegisterQueueClient(_queueClientSettings);
            var queueClient = subject.GetQueueClient(_queueClientSettings.QueueName);

            // assert
            result.Should().Be(true);
            queueClient.Should().NotBeNull();
        }

        [Fact(DisplayName = "Get unregistered queue client throws.")]
        public void Get_QueueClient_Throws()
        {
            // arrange
            var subject = new AzureQueueClientFactory();

            // act/assert
            subject.Invoking(x => x.GetQueueClient(_queueClientSettings.QueueName)).Should().Throw<InvalidOperationException>();
        }
    }
}