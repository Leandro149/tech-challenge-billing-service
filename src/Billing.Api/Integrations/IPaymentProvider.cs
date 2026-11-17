using Billing.Api.Contracts;

namespace Billing.Api.Integrations;

public interface IPaymentProvider
{
    Task<PaymentResponse> CreatePixAsync(CreatePixRequest input, string idempotencyKey, CancellationToken ct);
    Task<PaymentResponse> GetAsync(string orderId, CancellationToken ct);
    Task<PaymentFinancialState> GetFinancialStateAsync(string orderId, CancellationToken ct);
    Task RefundAsync(string orderId, string key, CancellationToken ct);
    Task CancelAsync(string orderId, string key, CancellationToken ct);
}

public sealed record PaymentFinancialState(PaymentResponse Payment, bool RefundConfirmed, bool RefundInProgress);
