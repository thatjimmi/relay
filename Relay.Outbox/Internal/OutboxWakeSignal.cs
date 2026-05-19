using System.Threading.Channels;

namespace Relay.Outbox.Internal;

/// <summary>
/// A lightweight signal that allows waking an outbox dispatcher immediately
/// when a new message is stored. Uses a bounded channel with DropWrite
/// to coalesce multiple rapid wakes into a single dispatch cycle.
/// </summary>
internal sealed class OutboxWakeSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Wake() => _channel.Writer.TryWrite(0);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Either the fallback timer elapsed or the host is stopping — both are fine.
        }
    }
}
