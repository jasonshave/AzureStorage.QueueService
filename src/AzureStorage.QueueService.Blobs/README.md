# AzureStorage.QueueService.Blobs

Send Azure Storage queue messages bigger than the queue's 64 KiB limit. This package plugs Azure Blob storage into [`AzureStorage.QueueService`](https://www.nuget.org/packages/AzureStorage.QueueService): a body too big for the queue is uploaded to a blob, and the queue message carries only the blob's address (the claim-check pattern). Processors and receivers download the body before your handler sees it, so handlers don't change. The blob is deleted once the message is completed, and kept when the message is dead-lettered so a replay still has its body.

## Install

Add the NuGet package [`AzureStorage.QueueService.Blobs`](https://www.nuget.org/packages/AzureStorage.QueueService.Blobs). It needs `AzureStorage.QueueService` 3.1 or later.

## Register

Attach it to each client. **Every client that reads the queue (and its poison queue) needs the same store**, or it can't read those messages and dead-letters them.

```csharp
builder.Services.AddAzureStorageQueueClient(queues =>
{
    // 1. Same storage account and credentials as the queue, container "queue-large-messages"
    queues.AddClient("orders", builder.Configuration.GetSection("Queues:Orders"))
          .UseBlobStorageForLargeMessages();

    // 2. A container you build yourself
    queues.AddClient("reports", builder.Configuration.GetSection("Queues:Reports"))
          .UseBlobStorageForLargeMessages(new BlobContainerClient(new Uri("https://myaccount.blob.core.windows.net/reports-bodies"), new DefaultAzureCredential()));

    // 3. A container built from your services the first time the client is used
    queues.AddClient("invoices", builder.Configuration.GetSection("Queues:Invoices"))
          .UseBlobStorageForLargeMessages(sp => sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("invoice-bodies"));
});
```

The first form works out the Blob endpoint from the queue client's own settings: a connection string is used as is, `https://myaccount.queue.core.windows.net/...` becomes `https://myaccount.blob.core.windows.net/`, and the storage emulator works too. For a custom domain, use the second or third form. Every form creates the container the first time a large message is sent (pass `createContainerIfNotExists: false` if it already exists), and takes an optional `thresholdBytes` to send smaller bodies to Blob storage as well.

Large messages travel in the library's envelope, which only 3.1 and later read: upgrade every consumer of the queue before turning this on.

## Clean up blobs that are left behind

Most blobs are deleted automatically, but some are not: a message that expires on the queue, or is removed by clearing the queue, is never processed, so its blob stays; and a send that fails after the upload deletes its blob only on a best-effort basis. Add an Azure Storage [lifecycle management rule](https://learn.microsoft.com/azure/storage/blobs/lifecycle-management-overview) that deletes blobs in the container older than the queue's message time to live plus a margin, for example 9 days for the default 7-day time to live. If you replay dead-lettered messages later, make it long enough to cover that too.

## Permissions

The identity the client connects with needs **Storage Blob Data Contributor** on the container (or the storage account) to create the container and to write, read and delete blobs. A connection string with the account key needs nothing more. An identity that isn't allowed to create containers needs the container created ahead of time, and `createContainerIfNotExists: false` on the registration, for example `.UseBlobStorageForLargeMessages("order-bodies", createContainerIfNotExists: false)`.

## Security

A queue message is untrusted input: anyone who can write to the queue can put any address in it. The store only reads and deletes blobs inside its own container, refuses every other address without making a request, and the message is dead-lettered. A SAS token on the container client is never written to the queue.
