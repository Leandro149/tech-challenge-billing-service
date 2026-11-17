using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Billing.Api.Contracts;
using Billing.Api.Integrations;
using Microsoft.Extensions.Options;

namespace Billing.Api.Saga;

public sealed class BillingSagaService(BillingSagaStore store, IServiceScopeFactory scopes,
    IOptions<BillingSagaOptions> options, ILogger<BillingSagaService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // A implantação mínima suporta uma réplica; serializa decisões e efeitos externos locais.
    private readonly SemaphoreSlim gate = new(1, 1);

    public BillingOrderResponse Get(Guid id) => Registered(id).ToResponse();

    public async Task ConsumeAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (routingKey is not ("os.criada" or "execution.falhou"))
            throw new BillingRuleException(400, "Evento não suportado pelo Billing.");
        var input = JsonSerializer.Deserialize<SagaEvent>(body.Span, Json);
        if (input is null || input.EventId == Guid.Empty || input.OrdemServicoUniqueCode == Guid.Empty ||
            input.OccurredAt == default || input.OccurredAt.Offset != TimeSpan.Zero)
            throw new BillingRuleException(400, "Evento deve conter eventId, ordemServicoUniqueCode e occurredAt UTC válidos.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            routingKey + JsonSerializer.Serialize(input, Json))));
        await gate.WaitAsync(ct);
        try
        {
            if (store.WasConsumed(input.EventId, fingerprint)) return;
            var order = store.Get(input.OrdemServicoUniqueCode) ?? new BillingOrder
                { OrdemServicoUniqueCode = input.OrdemServicoUniqueCode };
            if (routingKey == "os.criada") order.Registered = true;
            else
            {
                // Persiste inclusive antes de os.criada; um evento tardio não reabre a cobrança.
                order.CompensationRequested = true;
                if (order.CompensationStatus == "none") order.CompensationStatus = "pending";
                order.NextAttemptAt = DateTimeOffset.UtcNow;
            }
            // Inbox e agendamento da compensação são confirmados juntos antes do ACK.
            store.Save(order, consumedEvent: input.EventId, fingerprint: fingerprint);
        }
        finally { gate.Release(); }
    }

    public async Task<BillingOrderResponse> GenerateBudgetAsync(Guid id, decimal amount, CancellationToken ct)
    {
        if (amount <= 0 || amount > 999999999.99m || decimal.Round(amount, 2) != amount)
            throw new BillingRuleException(400, "Valor de orçamento inválido.");
        await gate.WaitAsync(ct);
        try
        {
            var order = Registered(id);
            if (order.CompensationRequested) throw Conflict("Execução falhou; não é possível gerar orçamento.");
            if (order.Amount is not null && order.Amount != amount) throw Conflict("Orçamento já gerado com outro valor.");
            order.Amount = amount;
            store.Save(order);
            return order.ToResponse();
        }
        finally { gate.Release(); }
    }

    public async Task<PaymentResponse> ApproveAsync(Guid id, ApproveBudgetRequest payer, string key, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var order = Registered(id);
            if (order.Amount is null) throw Conflict("Gere o orçamento antes de aprovar.");
            if (order.Decision == "rejected" || order.CompensationRequested) throw Conflict("A OS não aceita aprovação.");
            if (order.PaymentKey is not null && (order.PaymentKey != key || order.PayerEmail != payer.PayerEmail ||
                order.PayerFirstName != payer.PayerFirstName))
                throw Conflict("Reutilize a mesma chave de idempotência e os mesmos dados da aprovação original.");
            if (store.FindKey(key) is { } owner && owner.OrdemServicoUniqueCode != id)
                throw Conflict("Chave de idempotência já usada em outra OS.");
            if (order.PaymentKey is null)
            {
                order.PaymentKey = key;
                order.PayerEmail = payer.PayerEmail;
                order.PayerFirstName = payer.PayerFirstName;
                order.Decision = "payment_pending";
                order.PaymentCreationPending = true;
                // A chave/payload sobrevivem a timeout ou queda entre POST e resposta.
                store.Save(order);
            }
            using var scope = scopes.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<MercadoPagoClient>();
            var payment = order.PaymentId is null
                ? await CreatePaymentAsync(order, provider, ct)
                : await provider.GetAsync(order.PaymentId, ct);
            ApplyPayment(order, payment);
            return payment;
        }
        finally { gate.Release(); }
    }

    public async Task<BillingOrderResponse> RejectAsync(Guid id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var order = Registered(id);
            if (order.Decision == "rejected") return order.ToResponse();
            if (order.Amount is null) throw Conflict("Gere o orçamento antes de reprovar.");
            if (order.PaymentKey is not null || order.CompensationRequested)
                throw Conflict("Orçamento com pagamento iniciado ou compensação não pode ser reprovado.");
            order.Decision = "rejected";
            store.Save(order, new("billing.reprovado", new(Guid.NewGuid(), id, DateTimeOffset.UtcNow)));
            return order.ToResponse();
        }
        finally { gate.Release(); }
    }

    public async Task ObservePaymentAsync(PaymentResponse payment, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var order = store.FindPayment(payment.Id);
            // Cobre o webhook que chega antes da resposta ao POST, usando a referência reservada.
            if (order is null && Guid.TryParse(payment.OrderId, out var id))
            {
                var candidate = store.Get(id);
                if (candidate is { PaymentCreationPending: true }) order = candidate;
            }
            if (order is not null) ApplyPayment(order, payment);
        }
        finally { gate.Release(); }
    }

    public async Task<BillingOrderResponse> RetryReconciliationAsync(Guid id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var order = Registered(id);
            if (order.PaymentKey is null && !order.CompensationRequested) throw Conflict("A OS não possui operação pendente.");
            if (order.CompensationStatus == "manual_review")
                order.CompensationStatus = order.CompensationRequested ? "pending" : "none";
            order.Failures = 0;
            order.LastError = null;
            order.NextAttemptAt = DateTimeOffset.UtcNow;
            store.Save(order);
            return order.ToResponse();
        }
        finally { gate.Release(); }
    }

    private void ApplyPayment(BillingOrder order, PaymentResponse payment)
    {
        if (payment.OrderId != order.OrdemServicoUniqueCode.ToString("D") || payment.Amount != order.Amount ||
            (order.PaymentId is not null && order.PaymentId != payment.Id))
            throw Conflict("Pagamento não corresponde à OS e ao valor do orçamento.");
        order.PaymentId = payment.Id;
        order.PaymentCreationPending = false;
        order.Failures = 0;
        order.LastError = null;
        SagaOutgoingEvent? outgoing = null;
        if (payment.Status == "processed" && payment.StatusDetail == "accredited" &&
            !order.CompensationRequested && order.Decision != "rejected" && order.ApprovalEventId is null)
        {
            order.Decision = "approved";
            order.ApprovalEventId = Guid.NewGuid();
            outgoing = new("billing.aprovado", new(order.ApprovalEventId.Value,
                order.OrdemServicoUniqueCode, DateTimeOffset.UtcNow));
        }
        if (payment.Status is "canceled" or "failed" or "expired")
        {
            // Estado financeiro terminal não representa uma rejeição explícita de orçamento.
            if (order.Decision != "approved") order.Decision = "payment_unavailable";
        }
        store.Save(order, outgoing);
    }

    private static Task<PaymentResponse> CreatePaymentAsync(BillingOrder order, MercadoPagoClient provider, CancellationToken ct) =>
        provider.CreatePixAsync(new CreatePixRequest
        {
            OrderId = order.OrdemServicoUniqueCode.ToString("D"), Amount = order.Amount!.Value,
            PayerEmail = order.PayerEmail!, PayerFirstName = order.PayerFirstName
        }, order.PaymentKey!, ct);

    public async Task ReconcilePendingAsync(CancellationToken ct)
    {
        foreach (var candidate in store.PendingOrders())
        {
            if (candidate.NextAttemptAt > DateTimeOffset.UtcNow) continue;
            await gate.WaitAsync(ct);
            try
            {
                var order = store.Get(candidate.OrdemServicoUniqueCode)!;
                if (order.CompensationStatus == "manual_review") continue;
                using var scope = scopes.CreateScope();
                var provider = scope.ServiceProvider.GetRequiredService<MercadoPagoClient>();
                try
                {
                    if (order.PaymentCreationPending)
                        ApplyPayment(order, await CreatePaymentAsync(order, provider, ct));
                    if (order.CompensationRequested)
                        await CompensateAsync(order, provider, ct);
                    else if (order.PaymentId is not null && order.Decision == "payment_pending")
                        ApplyPayment(order, await provider.GetAsync(order.PaymentId, ct));
                    order.Failures = 0;
                    order.LastError = null;
                    order.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(options.Value.PollSeconds);
                    store.Save(order);
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    order.Failures++;
                    order.LastError = exception.GetType().Name;
                    order.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(order.Failures, 6))));
                    if (order.Failures >= options.Value.MaxFailures) order.CompensationStatus = "manual_review";
                    store.Save(order);
                    logger.LogWarning("Reconciliação da OS {OrderId} pendente ({FailureType}), tentativa {Attempt}.",
                        order.OrdemServicoUniqueCode, exception.GetType().Name, order.Failures);
                }
            }
            finally { gate.Release(); }
        }
    }

    private async Task CompensateAsync(BillingOrder order, MercadoPagoClient provider, CancellationToken ct)
    {
        if (order.CompensationStatus is "refunded" or "cancelled" or "not_applicable") return;
        if (order.PaymentId is null)
        {
            if (order.PaymentKey is not null) throw Conflict("Resultado da criação do pagamento ainda não conhecido.");
            order.CompensationStatus = "not_applicable";
            return;
        }
        var financial = await provider.GetFinancialStateAsync(order.PaymentId, ct);
        if (financial.Payment.Id != order.PaymentId || financial.Payment.OrderId != order.OrdemServicoUniqueCode.ToString("D") ||
            financial.Payment.Amount != order.Amount) throw Conflict("Pagamento divergente durante compensação.");
        if (financial.RefundConfirmed) { order.CompensationStatus = "refunded"; return; }
        if (financial.Payment.Status == "canceled") { order.CompensationStatus = "cancelled"; return; }
        if (financial.RefundInProgress) { order.CompensationAction = "refund"; order.CompensationSubmitted = true; return; }

        var action = financial.Payment.Status == "processed" && financial.Payment.StatusDetail == "accredited"
            ? "refund" : financial.Payment.Status == "action_required" ? "cancel" : null;
        if (action is null) return; // Estado incerto: consultar de novo, sem presumir estorno.
        if (order.CompensationAction != action)
        {
            order.CompensationAction = action;
            order.CompensationSubmitted = false;
        }
        if (order.CompensationSubmitted) return;
        store.Save(order); // Chave e operação persistidas antes do efeito externo.
        var key = $"{action}-{order.CompensationKey:D}";
        if (action == "refund") await provider.RefundAsync(order.PaymentId, key, ct);
        else await provider.CancelAsync(order.PaymentId, key, ct);
        order.CompensationSubmitted = true;
        store.Save(order);
        // Nunca usar apenas o HTTP do POST como confirmação de estorno/cancelamento.
        var confirmed = await provider.GetFinancialStateAsync(order.PaymentId, ct);
        if (confirmed.Payment.Id != order.PaymentId || confirmed.Payment.Amount != order.Amount ||
            confirmed.Payment.OrderId != order.OrdemServicoUniqueCode.ToString("D")) throw Conflict("Confirmação financeira divergente.");
        if (confirmed.RefundConfirmed) order.CompensationStatus = "refunded";
        else if (confirmed.Payment.Status == "canceled") order.CompensationStatus = "cancelled";
    }

    private BillingOrder Registered(Guid id) => store.Get(id) is { Registered: true } order
        ? order : throw new BillingRuleException(404, "OS ainda não recebida pelo Billing em os.criada.");
    private static BillingRuleException Conflict(string message) => new(409, message);
}
