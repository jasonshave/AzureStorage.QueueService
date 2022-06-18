using AutoFixture;
using Azure.Storage.Queues.Models;
using FluentAssertions;
using JasonShave.AzureStorage.QueueService.Converters;
using JasonShave.AzureStorage.QueueService.Exceptions;
using System.Text;
using System.Text.Json;

namespace JasonShave.AzureStorage.QueueService.Tests;

public class JsonQueueMessageConverterTests
{
    [Fact(DisplayName = "Convert into object")]
    public void Convert_To_Object()
    {
        // arrange
        var fixture = new Fixture();
        var testObject = fixture.Create<TestObject>();
        var json = JsonSerializer.Serialize(testObject);
        var testObjectBytes = Encoding.UTF8.GetBytes(json);
        var queueMessage = QueuesModelFactory.QueueMessage("1", "2", new BinaryData(testObjectBytes), 1);
        var subject = new JsonQueueMessageConverter();

        // act
        var newTestObject = subject.Convert<TestObject>(queueMessage.Body);

        // assert
        testObject.Should().BeEquivalentTo(newTestObject);
    }

    [Fact(DisplayName = "Convert empty string returns null")]
    public void Convert_Empty_Binary_Returns_Null()
    {
        // arrange
        var subject = new JsonQueueMessageConverter();

        // act/assert
        subject.Invoking(x => x.Convert<TestObject>(string.Empty)).Should().Throw<DeserializationException>();
    }
}