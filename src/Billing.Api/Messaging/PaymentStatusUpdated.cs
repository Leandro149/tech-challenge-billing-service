using System.Security.Cryptography;
using System.Text.Json;
using Billing.Api.Contracts;

namespace Billing.Api.Messaging;

public sealed record PaymentStatusUpdated(
    string EventId,
    DateTimeOffset ObservedAt,
    string PaymentId,
    string? OrderId,
    decimal Amount,
    string Status,
    string? StatusDetail)
{
    public const string EventType = "payment.status.updated.v1";

    public static PaymentStatusUpdated From(PaymentResponse payment)
    {
        // O mesmo estado recebe o mesmo ID em reentregas; o consumidor deve deduplicar.
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            EventType, payment.Id, payment.Status, payment.StatusDetail
        });
        var eventId = Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        return new(eventId, DateTimeOffset.UtcNow, payment.Id, payment.OrderId,
            payment.Amount, payment.Status, payment.StatusDetail);
    }
}
