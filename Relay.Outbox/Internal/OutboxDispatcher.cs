using System.Text.Json;
using Microsoft.Extensions.Logging;
using Relay.Outbox.Core;

namespace Relay.Outbox.Internal;

internal sealed class OutboxDispatcher(
    OutboxStoreResolver storeResolver,
    PublisherRegistry registry,
    IServiceProvider sp,
    OutboxOptions options,
    ILogger<OutboxDispatcher> logger) : IOutboxDispatcher
{
    public async Task<OutboxDispatchResult> DispatchPendingAsync(
        string outboxName,
        int batchSize = 50,
        CancellationToken ct = default)
    {
        var store = storeResolver.Resolve(outboxName);
        var messages = await store.GetPendingAsync(outboxName, batchSize, ct);
        var failures = new List<OutboxFailure>();
        var published = 0;

        foreach (var msg in messages)
        {
            if (ct.IsCancellationRequested) break;

            await store.MarkDispatchingAsync(msg.Id, ct);

            try
            {
                await DispatchAsync(msg, outboxName, ct);

                msg.PublishedAt = DateTime.UtcNow;
                await store.MarkPublishedAsync(msg.Id, ct);

                if (options.OnPublished is not null)
                    await options.OnPublished(msg);

                published++;
                logger.LogDebug(
                    "Published outbox message {Id} [{Type}] in {Outbox}",
                    msg.Id, msg.Type, outboxName);
            }
            catch (Exception ex)
            {
                var nextRetry = msg.RetryCount + 1;
                var willDeadLetter = nextRetry >= options.MaxRetries;

                logger.LogWarning(ex,
                    "Failed outbox message {Id} [{Type}] attempt {Attempt}/{Max} in {Outbox}",
                    msg.Id, msg.Type, nextRetry, options.MaxRetries, outboxName);

                if (options.OnFailed is not null)
                    await options.OnFailed(msg, ex);

                if (willDeadLetter)
                {
                    await store.MarkDeadLetteredAsync(msg.Id, ex.Message, ct);

                    if (options.OnDeadLettered is not null)
                        await options.OnDeadLettered(msg, ex);

                    logger.LogError(ex,
                        "Dead-lettered outbox message {Id} [{Type}] in {Outbox} after {Max} attempts",
                        msg.Id, msg.Type, outboxName, options.MaxRetries);
                }
                else
                {
                    await store.MarkFailedAsync(msg.Id, ex.Message, nextRetry, ct);
                }

                failures.Add(new OutboxFailure(msg.Id, msg.Type, ex.Message, willDeadLetter));
            }
        }

        return new OutboxDispatchResult
        {
            Published = published,
            Failed = failures.Count,
            Failures = failures
        };
    }

    private async Task DispatchAsync(OutboxMessage msg, string outboxName, CancellationToken ct)
    {
        var messageType = registry.Resolve(outboxName, msg.Type)
            ?? throw new InvalidOperationException(
                $"No publisher registered for type '{msg.Type}' in outbox '{outboxName}'. " +
                $"Did you forget to call .WithPublisher<{msg.Type}, YourPublisher>()?");

        var publisherType = typeof(IOutboxPublisher<>).MakeGenericType(messageType);
        var publisher = sp.GetService(publisherType)
            ?? throw new InvalidOperationException(
                $"Publisher for '{msg.Type}' is registered but could not be resolved from DI.");

        var payload = JsonSerializer.Deserialize(msg.Payload, messageType)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize payload for '{msg.Type}'.");

        var publishMethod = publisherType.GetMethod(nameof(IOutboxPublisher<object>.PublishAsync))!;
        await (Task)publishMethod.Invoke(publisher, [payload, msg, ct])!;
    }
}
