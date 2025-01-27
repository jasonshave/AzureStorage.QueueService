using System.Net.Http.Json;
using AzureStorage.QueueService.Tests.Server;
using FluentAssertions;
using Xunit.Abstractions;

namespace AzureStorage.QueueClient.IntegrationTests;

public class IntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
    private readonly HttpClient _httpClient;

    public IntegrationTests(ITestOutputHelper testOutputHelper, TestWebApplicationFactory<Program> factory)
    {
        factory.TestOutputHelper = testOutputHelper;
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