
using Xunit.Abstractions;

namespace AzureStorage.QueueClient.IntegrationTests
{
    public class IntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
    {
        private readonly HttpClient _httpClient;

        public IntegrationTests(ITestOutputHelper testOutputHelper, TestWebApplicationFactory<Program> factory)
        {
            _httpClient = factory.CreateClient();
        }

        [Fact]
        public async Task Test1()
        {
            // arrange
            

            // act
            _httpClient.PostAsync("/send", new StringContent("Hello world!"));

            // assert
        }
    }
}