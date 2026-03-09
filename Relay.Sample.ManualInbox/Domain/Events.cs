namespace Relay.Sample.ManualInbox.Domain;

// ---------------------------------------------------------------------------
// Inbound events — arrive at the system boundary from a payment processor webhook.
// Note: no IInboxHandler<T> implementations anywhere in this project.
// ---------------------------------------------------------------------------

/// <summary>
/// Fired by the payment gateway when a charge succeeds.
/// </summary>
public record PaymentReceived(
    string PaymentId,
    string CustomerId,
    decimal Amount,
    string Currency);

/// <summary>
/// Fired by the payment gateway when a refund is issued.
/// </summary>
public record RefundIssued(
    string RefundId,
    string OriginalPaymentId,
    decimal Amount,
    string Currency);
