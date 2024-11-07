using System.Net.Http.Json;
using AzureStorage.QueueService.Tests.Server;
using FluentAssertions;

namespace AzureStorage.QueueClient.IntegrationTests;

public class IntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
    private readonly HttpClient _httpClient;

    public IntegrationTests(TestWebApplicationFactory<Program> factory)
    {
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task SendMessage_SendsAnd_ReturnsSameMessage()
    {
        // arrange
        var person = new Person()
        {
            FirstName = "Jason",
        };

        // act
        var response = await _httpClient.PostAsJsonAsync("/send", person);
        var personResponse = await response.Content.ReadFromJsonAsync<Person>();

        // assert
        response.IsSuccessStatusCode.Should().BeTrue();
        personResponse.Should().NotBeNull();
        personResponse!.FirstName.Should().Be(person.FirstName);
    }
}