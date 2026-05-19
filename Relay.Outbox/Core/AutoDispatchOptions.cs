namespace Relay.Outbox.Core;

/// <summary>
/// Configuration for the built-in auto-dispatch background service.
/// </summary>
public sealed class AutoDispatchOptions
{
    /// <summary>
    /// How often to poll for pending messages when no wake signal is received.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan FallbackInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of messages to dispatch per sweep. Default: 50.
    /// </summary>
    public int BatchSize { get; set; } = 50;
}
