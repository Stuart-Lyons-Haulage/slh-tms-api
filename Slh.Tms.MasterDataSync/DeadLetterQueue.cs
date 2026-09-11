using System.Text;
using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;

namespace Slh.Tms.MasterDataSync;

public sealed class DeadLetterQueue(IOptions<SyncOptions> options)
{
    private readonly string queueName = options.Value.DeadLetterQueueName;

    public async Task EnqueueAsync(MasterListDefinition definition, SharePointItem item, Exception error, CancellationToken ct)
    {
        var storage = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
            ?? throw new InvalidOperationException("AzureWebJobsStorage is not configured.");
        var queue = new QueueClient(storage, queueName);
        await queue.CreateIfNotExistsAsync(cancellationToken: ct);
        var payload = JsonSerializer.Serialize(new
        {
            list = definition.Key,
            sharePointList = definition.ListName,
            sharePointItemId = item.Id,
            item.Fields,
            error = error.Message,
            failedAtUtc = DateTime.UtcNow
        });
        await queue.SendMessageAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)), cancellationToken: ct);
    }
}
