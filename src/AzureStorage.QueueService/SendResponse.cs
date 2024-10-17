namespace AzureStorage.QueueService;

public record SendResponse(string Receipt, string MessageId);