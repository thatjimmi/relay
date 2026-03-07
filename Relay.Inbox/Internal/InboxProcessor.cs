using System.Text.Json;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;

namespace Relay.Inbox.Internal;

internal sealed class InboxProcessor(
    IInboxStore store,
    HandlerRegistry registry,
    IServiceProvider sp,
    InboxOptions options,
    ILogger<InboxProcessor> logger) : IInboxProcessor
{
    public async Task<InboxProcessResult> ProcessPendingAsync(
        string inboxName,
        int batchSize = 50,
        CancellationToken ct = default)
    {
        var messages = await store.GetPendingAsync(inboxName, batchSize, ct);
        var failures = new List<InboxFailure>();
        var processed = 0;

        foreach (var msg in messages)
        {
            if (ct.IsCancellationRequested) break;

            await store.MarkProcessingAsync(msg.Id, ct);

            try
            {
                await DispatchAsync(msg, inboxName, ct);

                msg.ProcessedAt = DateTime.UtcNow;
                await store.MarkProcessedAsync(msg.Id, ct);

                if (options.OnProcessed is not null)
                    await options.OnProcessed(msg);

                processed++;
                logger.LogDebug("Processed inbox message {Id} [{Type}] in {Inbox}", msg.Id, msg.Type, inboxName);
            }
            catch (Exception ex)
            {
                var nextRetry = msg.RetryCount + 1;
                var willDeadLetter = nextRetry >= options.MaxRetries;

                logger.LogWarning(ex,
                    "Failed inbox message {Id} [{Type}] attempt {Attempt}/{Max} in {Inbox}",
                    msg.Id, msg.Type, nextRetry, options.MaxRetries, inboxName);

                if (options.OnFailed is not null)
                    await options.OnFailed(msg, ex);

                if (willDeadLetter)
                {
                    await store.MarkDeadLetteredAsync(msg.Id, ex.Message, ct);

                    if (options.OnDeadLettered is not null)
                        await options.OnDeadLettered(msg, ex);

                    logger.LogError(ex,
                        "Dead-lettered inbox message {Id} [{Type}] in {Inbox} after {Max} attempts",
                        msg.Id, msg.Type, inboxName, options.MaxRetries);
                }
                else
                {
                    await store.MarkFailedAsync(msg.Id, ex.Message, nextRetry, ct);
                }

                failures.Add(new InboxFailure(msg.Id, msg.Type, ex.Message, willDeadLetter));
            }
        }

        return new InboxProcessResult
        {
            Processed = processed,
            Failed = failures.Count,
            Failures = failures
        };
    }

    private async Task DispatchAsync(InboxMessage msg, string inboxName, CancellationToken ct)
    {
        var messageType = registry.Resolve(inboxName, msg.Type)
            ?? throw new InvalidOperationException(
                $"No handler registered for type '{msg.Type}' in inbox '{inboxName}'. " +
                $"Did you forget to call .WithHandler<{msg.Type}, YourHandler>()?");

        var handlerType = typeof(IInboxHandler<>).MakeGenericType(messageType);
        var handler = sp.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"Handler for '{msg.Type}' is registered but could not be resolved from DI.");

        var payload = JsonSerializer.Deserialize(msg.Payload, messageType)
            ?? throw new InvalidOperationException($"Failed to deserialize payload for '{msg.Type}'.");

        var handleMethod = handlerType.GetMethod(nameof(IInboxHandler<object>.HandleAsync))!;
        using var correlationScope = options.CorrelationScope?.Invoke(msg.Id.ToString());
        await (Task)handleMethod.Invoke(handler, [payload, ct])!;
    }
}
