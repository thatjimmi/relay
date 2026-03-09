using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Relay.Inbox.Core;
using Relay.Inbox.Extensions;
using Relay.Inbox.Storage;

namespace Relay.Sample.ManualInbox.Tests;

// ---------------------------------------------------------------------------
// Domain events — same shape as Relay.Sample.ManualInbox.Domain
// (kept local to avoid referencing the Azure Functions host project)
// ---------------------------------------------------------------------------

public record PaymentReceived(
    string PaymentId,
    string CustomerId,
    decimal Amount,
    string Currency);

public record RefundIssued(
    string RefundId,
    string OriginalPaymentId,
    decimal Amount,
    string Currency);

// ===========================================================================
// Tests — exercises the IInboxClient "raw / manual" flow used by the
// ManualInbox Azure Functions sample: receive → get pending → process manually.
// ===========================================================================

public class ManualInboxClientTests
{
    private const string Channel = "payments";

    private static (InMemoryInboxStore store, IInboxClient client) Build(Action<InboxOptions>? configure = null)
    {
        var store = new InMemoryInboxStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInboxClient(configure);
        services.UseInboxStore(store);

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IInboxClient>();

        return (store, client);
    }

    // ── Receive ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Receive_payment_stores_message()
    {
        var (store, client) = Build();
        var payment = new PaymentReceived("PAY-001", "CUST-A", 49.99m, "USD");

        var result = await client.ReceiveAsync(
            Channel, payment, $"payment:{payment.PaymentId}", "gateway");

        Assert.True(result.Accepted);
        Assert.False(result.WasDuplicate);
        Assert.NotNull(result.MessageId);
        Assert.Single(store.All);
        Assert.Equal(Channel, store.All[0].InboxName);
        Assert.Equal(nameof(PaymentReceived), store.All[0].Type);
    }

    [Fact]
    public async Task Receive_refund_stores_message()
    {
        var (store, client) = Build();
        var refund = new RefundIssued("REF-001", "PAY-001", 49.99m, "USD");

        var result = await client.ReceiveAsync(
            Channel, refund, $"refund:{refund.RefundId}", "gateway");

        Assert.True(result.Accepted);
        Assert.Single(store.All);
        Assert.Equal(nameof(RefundIssued), store.All[0].Type);
    }

    [Fact]
    public async Task Duplicate_payment_is_detected()
    {
        var (store, client) = Build();
        var payment = new PaymentReceived("PAY-001", "CUST-A", 49.99m, "USD");
        var key = $"payment:{payment.PaymentId}";

        await client.ReceiveAsync(Channel, payment, key);
        var second = await client.ReceiveAsync(Channel, payment, key);

        Assert.True(second.WasDuplicate);
        Assert.Single(store.All);
    }

    [Fact]
    public async Task Different_payments_are_independent()
    {
        var (store, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");
        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-002", "CUST-B", 20m, "EUR"), "payment:PAY-002");
        await client.ReceiveAsync(Channel,
            new RefundIssued("REF-001", "PAY-001", 10m, "USD"), "refund:REF-001");

        Assert.Equal(3, store.All.Count);
    }

    [Fact]
    public async Task Stores_source_tag()
    {
        var (store, client) = Build();
        var payment = new PaymentReceived("PAY-001", "CUST-A", 49.99m, "USD");

        await client.ReceiveAsync(Channel, payment, "payment:PAY-001", "payment-gateway");

        Assert.Equal("payment-gateway", store.All.Single().Source);
    }

    [Fact]
    public async Task Payload_is_valid_json()
    {
        var (store, client) = Build();
        var payment = new PaymentReceived("PAY-001", "CUST-A", 49.99m, "USD");

        await client.ReceiveAsync(Channel, payment, "payment:PAY-001");

        var deserialized = JsonSerializer.Deserialize<PaymentReceived>(store.All[0].Payload);
        Assert.NotNull(deserialized);
        Assert.Equal("PAY-001", deserialized!.PaymentId);
        Assert.Equal(49.99m, deserialized.Amount);
    }

    // ── Process (manual) ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPending_returns_received_messages()
    {
        var (_, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");
        await client.ReceiveAsync(Channel,
            new RefundIssued("REF-001", "PAY-001", 10m, "USD"), "refund:REF-001");

        var pending = await client.GetPendingAsync(Channel);

        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public async Task MarkProcessed_transitions_status()
    {
        var (store, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        var pending = await client.GetPendingAsync(Channel);
        Assert.Single(pending);

        await client.MarkProcessedAsync(pending[0]);

        Assert.Equal(InboxMessageStatus.Processed, store.All.Single().Status);
        Assert.NotNull(store.All.Single().ProcessedAt);
    }

    [Fact]
    public async Task Processed_messages_not_returned_by_GetPending()
    {
        var (_, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");
        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-002", "CUST-B", 20m, "EUR"), "payment:PAY-002");

        var pending = await client.GetPendingAsync(Channel);
        await client.MarkProcessedAsync(pending[0]); // process first one

        var remaining = await client.GetPendingAsync(Channel);
        Assert.Single(remaining);
        Assert.Contains("PAY-002", remaining[0].Payload);
    }

    [Fact]
    public async Task MarkFailed_increments_retry_count()
    {
        var (store, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        var pending = await client.GetPendingAsync(Channel);
        await client.MarkFailedAsync(pending[0], "Connection timeout", maxRetries: 5);

        var msg = store.All.Single();
        Assert.Equal(InboxMessageStatus.Failed, msg.Status);
        Assert.Equal(1, msg.RetryCount);
        Assert.Equal("Connection timeout", msg.Error);
    }

    [Fact]
    public async Task Failed_messages_are_returned_by_GetPending_for_retry()
    {
        var (_, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        var pending = await client.GetPendingAsync(Channel);
        await client.MarkFailedAsync(pending[0], "Transient error", maxRetries: 5);

        var retryBatch = await client.GetPendingAsync(Channel);
        Assert.Single(retryBatch); // failed messages are retried
    }

    [Fact]
    public async Task Dead_letters_after_max_retries()
    {
        var deadLettered = new List<InboxMessage>();
        var (store, client) = Build(o =>
            o.OnDeadLettered = (msg, _) => { deadLettered.Add(msg); return Task.CompletedTask; });

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        const int maxRetries = 3;
        for (var i = 0; i < maxRetries; i++)
        {
            var batch = await client.GetPendingAsync(Channel);
            Assert.Single(batch);
            await client.MarkFailedAsync(batch[0], $"Failure #{i + 1}", maxRetries: maxRetries);
        }

        var msg = store.All.Single();
        Assert.Equal(InboxMessageStatus.DeadLettered, msg.Status);
        Assert.Single(deadLettered);
    }

    [Fact]
    public async Task Dead_lettered_messages_not_returned_by_GetPending()
    {
        var (_, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        // Exhaust retries
        for (var i = 0; i < 3; i++)
        {
            var batch = await client.GetPendingAsync(Channel);
            if (batch.Count == 0) break;
            await client.MarkFailedAsync(batch[0], "Error", maxRetries: 3);
        }

        var pending = await client.GetPendingAsync(Channel);
        Assert.Empty(pending);
    }

    // ── Hooks ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnMessageStored_fires_on_new_message()
    {
        var stored = new List<InboxMessage>();
        var (_, client) = Build(o =>
            o.OnMessageStored = msg => { stored.Add(msg); return Task.CompletedTask; });

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        Assert.Single(stored);
        Assert.Equal(Channel, stored[0].InboxName);
    }

    [Fact]
    public async Task OnMessageStored_not_called_for_duplicate()
    {
        var stored = new List<InboxMessage>();
        var (_, client) = Build(o =>
            o.OnMessageStored = msg => { stored.Add(msg); return Task.CompletedTask; });

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");
        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        Assert.Single(stored); // only once
    }

    [Fact]
    public async Task OnProcessed_fires_when_marked_processed()
    {
        var processed = new List<InboxMessage>();
        var (_, client) = Build(o =>
            o.OnProcessed = msg => { processed.Add(msg); return Task.CompletedTask; });

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        var pending = await client.GetPendingAsync(Channel);
        await client.MarkProcessedAsync(pending[0]);

        Assert.Single(processed);
    }

    [Fact]
    public async Task OnDuplicate_fires_on_duplicate()
    {
        var duplicateKeys = new List<string>();
        var (_, client) = Build(o =>
            o.OnDuplicate = key => { duplicateKeys.Add(key); return Task.CompletedTask; });

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");
        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        Assert.Single(duplicateKeys);
    }

    // ── Manual processing flow (what ProcessInboxFunction does) ────────────

    [Fact]
    public async Task Full_manual_processing_flow()
    {
        var (store, client) = Build();

        // 1. Receive — simulates HTTP trigger inserting into inbox
        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 49.99m, "USD"),
            "payment:PAY-001", "azure-functions-http");
        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-002", "CUST-B", 120.00m, "EUR"),
            "payment:PAY-002", "azure-functions-http");
        await client.ReceiveAsync(Channel,
            new RefundIssued("REF-001", "PAY-001", 49.99m, "USD"),
            "refund:REF-001", "azure-functions-http");

        Assert.Equal(3, store.All.Count);
        Assert.All(store.All, m => Assert.Equal(InboxMessageStatus.Pending, m.Status));

        // 2. Get pending — simulates timer trigger fetching batch
        var messages = await client.GetPendingAsync(Channel, batchSize: 50);
        Assert.Equal(3, messages.Count);

        // 3. Process each manually — simulates switch on msg.Type
        var processedPayments = new List<PaymentReceived>();
        var processedRefunds = new List<RefundIssued>();

        foreach (var msg in messages)
        {
            switch (msg.Type)
            {
                case nameof(PaymentReceived):
                    processedPayments.Add(JsonSerializer.Deserialize<PaymentReceived>(msg.Payload)!);
                    break;
                case nameof(RefundIssued):
                    processedRefunds.Add(JsonSerializer.Deserialize<RefundIssued>(msg.Payload)!);
                    break;
            }
            await client.MarkProcessedAsync(msg);
        }

        Assert.Equal(2, processedPayments.Count);
        Assert.Single(processedRefunds);
        Assert.All(store.All, m => Assert.Equal(InboxMessageStatus.Processed, m.Status));

        // 4. No more pending
        var remaining = await client.GetPendingAsync(Channel);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Partial_failure_in_batch_processes_remaining()
    {
        var (store, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");
        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-002", "CUST-B", 20m, "EUR"), "payment:PAY-002");
        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-003", "CUST-C", 30m, "GBP"), "payment:PAY-003");

        var messages = await client.GetPendingAsync(Channel);

        // Process first, fail second, process third — like the timer function would
        await client.MarkProcessedAsync(messages[0]);
        await client.MarkFailedAsync(messages[1], "Transient error", maxRetries: 5);
        await client.MarkProcessedAsync(messages[2]);

        var processedCount = store.All.Count(m => m.Status == InboxMessageStatus.Processed);
        var failedCount = store.All.Count(m => m.Status == InboxMessageStatus.Failed);

        Assert.Equal(2, processedCount);
        Assert.Equal(1, failedCount);
    }

    // ── Batch size ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPending_respects_batch_size()
    {
        var (_, client) = Build();

        for (var i = 1; i <= 10; i++)
            await client.ReceiveAsync(Channel,
                new PaymentReceived($"PAY-{i:D3}", "CUST", i * 10m, "USD"),
                $"payment:PAY-{i:D3}");

        var batch = await client.GetPendingAsync(Channel, batchSize: 3);
        Assert.Equal(3, batch.Count);
    }

    // ── Source timestamp updates ───────────────────────────────────────────

    [Fact]
    public async Task Newer_source_timestamp_updates_payload()
    {
        var (store, client) = Build();
        var t1 = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc);

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 49.99m, "USD"),
            "payment:PAY-001", t1);

        var result = await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 99.99m, "USD"),
            "payment:PAY-001", t2);

        Assert.True(result.Accepted);
        Assert.True(result.WasUpdated);
        Assert.Single(store.All);
        Assert.Contains("99.99", store.All.Single().Payload);
    }

    [Fact]
    public async Task Older_source_timestamp_treated_as_duplicate()
    {
        var (store, client) = Build();
        var t1 = new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc);
        var t0 = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 49.99m, "USD"),
            "payment:PAY-001", t1);

        var result = await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10.00m, "USD"),
            "payment:PAY-001", t0);

        Assert.True(result.WasDuplicate);
        Assert.Contains("49.99", store.All.Single().Payload); // unchanged
    }

    // ── Stats & requeue ────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_reflect_correct_counts()
    {
        var (store, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");
        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-002", "CUST-B", 20m, "EUR"), "payment:PAY-002");

        var pending = await client.GetPendingAsync(Channel, batchSize: 1);
        await client.MarkProcessedAsync(pending[0]);

        var stats = await store.GetStatsAsync(Channel);

        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.Processed);
    }

    [Fact]
    public async Task Requeue_dead_lettered_message()
    {
        var (store, client) = Build();

        await client.ReceiveAsync(Channel,
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");

        // Dead-letter it
        for (var i = 0; i < 3; i++)
        {
            var batch = await client.GetPendingAsync(Channel);
            if (batch.Count == 0) break;
            await client.MarkFailedAsync(batch[0], "Error", maxRetries: 3);
        }

        Assert.Equal(InboxMessageStatus.DeadLettered, store.All.Single().Status);

        // Requeue
        var requeued = await store.RequeueAsync(store.All.Single().Id);
        Assert.True(requeued);
        Assert.Equal(InboxMessageStatus.Pending, store.All.Single().Status);

        // Can be fetched again
        var pending = await client.GetPendingAsync(Channel);
        Assert.Single(pending);
    }

    // ── Isolation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Different_inbox_names_are_isolated()
    {
        var (store, client) = Build();

        await client.ReceiveAsync("payments",
            new PaymentReceived("PAY-001", "CUST-A", 10m, "USD"), "payment:PAY-001");
        await client.ReceiveAsync("refunds",
            new RefundIssued("REF-001", "PAY-001", 10m, "USD"), "refund:REF-001");

        var paymentsPending = await client.GetPendingAsync("payments");
        var refundsPending = await client.GetPendingAsync("refunds");

        Assert.Single(paymentsPending);
        Assert.Single(refundsPending);
        Assert.Equal(nameof(PaymentReceived), paymentsPending[0].Type);
        Assert.Equal(nameof(RefundIssued), refundsPending[0].Type);
    }
}
