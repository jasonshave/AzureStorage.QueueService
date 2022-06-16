using AutoFixture;
using FluentAssertions;
using JasonShave.AzureStorage.QueueService.Converters;
using JasonShave.AzureStorage.QueueService.Exceptions;

namespace AzureStorage.QueueService.Tests;

public class JsonQueueMessageConverterTests
{
    [Fact(DisplayName = "Convert binary into object")]
    public void Convert_Binary_To_Object()
    {
        // arrange
        var fixture = new Fixture();
        var testObject = fixture.Create<TestObject>();
        var binaryTestObject = new BinaryData(testObject);
        var subject = new JsonQueueMessageConverter();

        // act
        var newTestObject = subject.Convert<TestObject>(binaryTestObject);

        // assert
        testObject.Should().BeEquivalentTo(newTestObject);
    }

    [Fact(DisplayName = "Convert empty binary returns null")]
    public void Convert_Empty_Binary_Returns_Null()
    {
        // arrange
        var binaryTestObject = new BinaryData(Array.Empty<byte>());
        var subject = new JsonQueueMessageConverter();

        // act/assert
        subject.Invoking(x => x.Convert<TestObject>(binaryTestObject)).Should().Throw<DeserializationException>();
    }
}