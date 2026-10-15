using System.Text;
using System.Text.Json;
using Azure.Storage.Queues.Models;
using FluentAssertions;

namespace AzureStorage.QueueService.Tests;

public class JsonQueueMessageConverterTests
{
    private static TestObject NewTestObject() => new() { Property1 = Guid.NewGuid().ToString(), Property2 = true, Property3 = Random.Shared.Next() };

    [Fact(DisplayName = "Convert into object")]
    public void Convert_To_Object()
    {
        // arrange
        var testObject = NewTestObject();
        var json = JsonSerializer.Serialize(testObject);
        var testObjectBytes = Encoding.UTF8.GetBytes(json);
        var queueMessage = QueuesModelFactory.QueueMessage("1", "2", new BinaryData(testObjectBytes), 1);
        var subject = new JsonQueueMessageConverter();

        // act
        var newTestObject = subject.Convert<TestObject>(queueMessage.Body);

        // assert
        testObject.Should().BeEquivalentTo(newTestObject);
    }

    [Fact(DisplayName = "Convert empty string throws with the serializer error attached")]
    public void Convert_Empty_String_Throws()
    {
        // arrange
        var subject = new JsonQueueMessageConverter();

        // act/assert
        subject.Invoking(x => x.Convert<TestObject>(string.Empty)).Should().Throw<DeserializationException>()
            .WithInnerException<JsonException>();
    }

    [Fact(DisplayName = "Convert Base64-encoded JSON (as written by older SDKs and Azure Functions)")]
    public void Convert_Base64_Encoded_Json()
    {
        // arrange
        var testObject = NewTestObject();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testObject)));
        var subject = new JsonQueueMessageConverter();

        // act
        var result = subject.Convert<TestObject>(encoded);

        // assert
        result.Should().BeEquivalentTo(testObject);
    }

    [Theory(DisplayName = "Short JSON values that are also valid Base64 are read as JSON")]
    [InlineData("1234", 1234)]
    [InlineData("true", true)]
    public void Convert_Short_Json_That_Looks_Like_Base64_Is_Not_Mangled<T>(string input, T expected)
    {
        // arrange
        var subject = new JsonQueueMessageConverter();

        // act/assert
        subject.Convert<T>(input).Should().Be(expected);
    }

    [Fact(DisplayName = "Convert from Object to BinaryData and back to Object")]
    public void Convert_To_And_From()
    {
        // arrange
        var testObject = NewTestObject();

        var subject = new JsonQueueMessageConverter();

        // act
        BinaryData binaryData = subject.Convert(testObject);
        TestObject? convertedTestObject = subject.Convert<TestObject>(binaryData);

        // assert
        testObject.Should().BeEquivalentTo(convertedTestObject);
    }

    [Fact(DisplayName = "Custom serializer options are applied when sending as well as receiving")]
    public void Convert_Uses_Serializer_Options_When_Sending()
    {
        // arrange
        var testObject = new TestObject { Property1 = "value", Property2 = true, Property3 = 1 };
        var subject = new JsonQueueMessageConverter(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // act
        var json = subject.Convert(testObject).ToString();
        var roundTripped = subject.Convert<TestObject>(json);

        // assert
        json.Should().Contain("\"property1\"").And.NotContain("\"Property1\"");
        roundTripped.Should().BeEquivalentTo(testObject);
    }
}
