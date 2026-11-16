namespace Billing.Api.Contracts;

public sealed record PaymentResponse(
    string Id,
    string? OrderId,
    decimal Amount,
    string Status,
    string? StatusDetail,
    string? QrCode,
    string? QrCodeBase64,
    string? TicketUrl);
