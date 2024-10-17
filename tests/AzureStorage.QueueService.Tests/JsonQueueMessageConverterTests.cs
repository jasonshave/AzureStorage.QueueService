using System.Text;
using System.Text.Json;
using AutoFixture;
using Azure.Storage.Queues.Models;
using FluentAssertions;

namespace AzureStorage.QueueService.Tests;

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

    [Fact(DisplayName = "Convert object to BinaryData")]
    public void Convert_Object_To_BinaryData()
    {
        // arrange
        var fixture = new Fixture();
        var testObject = fixture.Create<TestObject>();

        var subject = new JsonQueueMessageConverter();

        // act
        BinaryData binaryData = subject.Convert(testObject);

        // assert
        binaryData.Should().BeOfType(typeof(BinaryData));
    }

    [Fact(DisplayName = "Convert from Object to BinaryData and back to Object")]
    public void Convert_To_And_From()
    {
        // arrange
        var fixture = new Fixture();
        var testObject = fixture.Create<TestObject>();

        var subject = new JsonQueueMessageConverter();

        // act
        BinaryData binaryData = subject.Convert(testObject);
        TestObject? convertedTestObject = subject.Convert<TestObject>(binaryData);

        // assert
        testObject.Should().BeEquivalentTo(convertedTestObject);
    }
}