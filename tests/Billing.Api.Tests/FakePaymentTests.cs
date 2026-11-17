using System.Net;
using System.Net.Http.Json;
using Billing.Api.Contracts;
using Billing.Api.Integrations;
using Billing.Api.Saga;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Billing.Api.Tests.BillingSagaTests;

namespace Billing.Api.Tests;

public sealed class FakePaymentTests
{
    [Fact]
    public async Task Fake_approval_is_idempotent_and_refund_survives_restarts_and_default_provider_change()
    {
        var path = Path.Combine(Path.GetTempPath(), "billing-tests", Guid.NewGuid() + ".db");
        var os = Guid.NewGuid();
        string paymentId;
        await using (var factory = new BillingFactory { PaymentProvider = "Fake", SagaDatabasePath = path, AccessToken = "" })
        {
            using var client = factory.CreateClient();
            await RegisterAsync(factory, os);
            await BudgetAsync(factory, os);
            using var approval = await client.SendAsync(Approval(os));
            approval.EnsureSuccessStatusCode();
            var payment = (await approval.Content.ReadFromJsonAsync<PaymentResponse>())!;
            paymentId = payment.Id;
            Assert.StartsWith("ORD-FAKE-", paymentId);
            Assert.Equal("processed", payment.Status);
            Assert.Equal("accredited", payment.StatusDetail);
            Assert.Equal(os.ToString("D"), payment.OrderId);
            Assert.Equal(50, payment.Amount);

            using var repeated = await client.SendAsync(Approval(os));
            Assert.Equal(payment, await repeated.Content.ReadFromJsonAsync<PaymentResponse>());
            using var conflicting = await client.SendAsync(Approval(os, email: "other@example.com"));
            Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
            var approved = Assert.Single(Store(factory).PendingEvents());
            Assert.Equal("billing.aprovado", approved.RoutingKey);
            Assert.Equal(os, approved.Event.OrdemServicoUniqueCode);
            Store(factory).MarkSent(approved.Event.EventId);
            Assert.Equal("Fake", Store(factory).Get(os)!.PaymentProvider);
            Assert.Equal(0, factory.Requests);
        }

        await using (var restarted = new BillingFactory { SagaDatabasePath = path, AccessToken = "" })
        {
            using var client = restarted.CreateClient();
            var failure = EventBody(os);
            var saga = Service(restarted);
            await saga.ConsumeAsync("execution.falhou", failure, default);
            Assert.Equal("pending", saga.Get(os).CompensationStatus);
            await saga.ReconcilePendingAsync(default);
            await saga.ConsumeAsync("execution.falhou", failure, default);
            await saga.ConsumeAsync("execution.falhou", EventBody(os), default);
            await saga.ReconcilePendingAsync(default);
            var order = (await client.GetFromJsonAsync<BillingOrderResponse>($"/api/billing/ordens/{os}"))!;
            Assert.Equal("refunded", order.CompensationStatus);
            Assert.Equal(paymentId, order.PaymentId);
            Assert.Null(order.LastError);
            Assert.Empty(Store(restarted).PendingEvents());
            Assert.Equal(0, restarted.Requests);
        }

        await using var observed = new BillingFactory { SagaDatabasePath = path, AccessToken = "" };
        using var observedClient = observed.CreateClient();
        var refunded = (await observedClient.GetFromJsonAsync<PaymentResponse>($"/api/payments/{paymentId}"))!;
        Assert.Equal("refunded", refunded.Status);
        Assert.Equal("refunded", refunded.StatusDetail);
        Assert.Equal(0, observed.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_payment_still_uses_mercado_pago_and_422_keeps_compensation_pending(bool creationPending)
    {
        var os = Guid.NewGuid();
        await using var factory = new BillingFactory { PaymentProvider = "Fake" };
        var requests = new List<string>();
        factory.Respond = (request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/v1/orders")
                Assert.Equal("legacy-key", Assert.Single(request.Headers.GetValues("X-Idempotency-Key")));
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/refund", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                : BillingFactory.JsonResponse(ProviderOrder(os, "processed", "accredited")));
        };
        Store(factory).Save(new BillingOrder
        {
            OrdemServicoUniqueCode = os, Registered = true, Amount = 50, PaymentId = creationPending ? null : "ORD123",
            PaymentKey = "legacy-key", PayerEmail = "legacy@example.com", PaymentCreationPending = creationPending,
            Decision = creationPending ? "payment_pending" : "approved"
        });
        await Service(factory).ConsumeAsync("execution.falhou", EventBody(os), default);
        await Service(factory).ReconcilePendingAsync(default);
        var order = Store(factory).Get(os)!;
        Assert.Equal("ORD123", order.PaymentId);
        Assert.Null(order.PaymentProvider);
        Assert.Equal("pending", order.CompensationStatus);
        Assert.Equal(nameof(MercadoPagoException), order.LastError);
        Assert.False(order.CompensationSubmitted);
        string[] expected = creationPending ? ["/v1/orders", "/v1/orders/ORD123", "/v1/orders/ORD123/refund"]
            : ["/v1/orders/ORD123", "/v1/orders/ORD123/refund"];
        Assert.Equal(expected, requests);
    }

    [Fact]
    public async Task Fake_cannot_confirm_or_refund_a_payment_it_did_not_create()
    {
        await using var factory = new BillingFactory { PaymentProvider = "Fake", AccessToken = "" };
        using var scope = factory.Services.CreateScope();
        var fake = scope.ServiceProvider.GetRequiredService<PaymentProviders>().Resolve("Fake");
        var failure = await Assert.ThrowsAsync<BillingRuleException>(async () => await fake.RefundAsync("ORD123", "refund", default));
        Assert.Equal(404, failure.StatusCode);
        var os = Guid.NewGuid();
        Store(factory).Save(new BillingOrder
        {
            OrdemServicoUniqueCode = os, Registered = true, Amount = 50, Decision = "approved",
            PaymentId = "ORD123", PaymentKey = "missing", PaymentProvider = "Fake"
        });
        await Service(factory).ConsumeAsync("execution.falhou", EventBody(os), default);
        await Service(factory).ReconcilePendingAsync(default);
        var order = Service(factory).Get(os);
        Assert.Equal("pending", order.CompensationStatus);
        Assert.Equal(nameof(BillingRuleException), order.LastError);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Standalone_pix_keeps_created_location_and_idempotency_with_fake_in_testing()
    {
        await using var factory = new BillingFactory { PaymentProvider = "Fake", EnvironmentName = "Testing", AccessToken = "" };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Idempotency-Key", "standalone");
        var body = new { orderId = "fixture", amount = 50, payerEmail = "test@example.com" };
        using var created = await client.PostAsJsonAsync("/api/payments/pix", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var payment = await created.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.Equal(payment, await client.GetFromJsonAsync<PaymentResponse>(created.Headers.Location));
        using var repeated = await client.PostAsJsonAsync("/api/payments/pix", body);
        Assert.Equal(payment, await repeated.Content.ReadFromJsonAsync<PaymentResponse>());
        using var conflict = await client.PostAsJsonAsync("/api/payments/pix", body with { amount = 51 });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(0, factory.Requests);
    }

    [Theory]
    [InlineData("Production", "Fake")]
    [InlineData("Staging", "Fake")]
    [InlineData("Development", "Unknown")]
    public void Invalid_provider_configuration_prevents_startup(string environment, string provider)
    {
        using var factory = new BillingFactory { PaymentProvider = provider, EnvironmentName = environment };
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Payment:Provider", error.Message);
    }
}
