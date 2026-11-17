namespace Billing.Api.Integrations;

// O serializador do cliente usa snake_case apenas para os contratos do provedor.
internal sealed class MercadoPagoOrder
{
    public string Id { get; init; } = "";
    public string? ExternalReference { get; init; }
    public string TotalAmount { get; init; } = "";
    public string Status { get; init; } = "";
    public string? StatusDetail { get; init; }
    public OrderTransactions? Transactions { get; init; }
}

internal sealed class OrderTransactions
{
    public List<OrderPayment>? Payments { get; init; }
    public List<OrderRefund>? Refunds { get; init; }
}

internal sealed class OrderRefund
{
    public string Status { get; init; } = "";
    public string Amount { get; init; } = "";
}

internal sealed class OrderPayment
{
    public PixPaymentMethod? PaymentMethod { get; init; }
}

internal sealed class PixPaymentMethod
{
    public string? QrCode { get; init; }
    public string? QrCodeBase64 { get; init; }
    public string? TicketUrl { get; init; }
}
