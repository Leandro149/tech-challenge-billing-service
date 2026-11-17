using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Billing.Api.Contracts;
using Billing.Api.Saga;
using Microsoft.Extensions.DependencyInjection;

namespace Billing.Api.Tests;

public sealed class BillingSagaTests
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static byte[] EventBody(Guid os, Guid? eventId = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new SagaEvent(eventId ?? Guid.NewGuid(), os, DateTimeOffset.UtcNow), Json);

    internal static BillingSagaService Service(BillingFactory factory) => factory.Services.GetRequiredService<BillingSagaService>();
    internal static BillingSagaStore Store(BillingFactory factory) => factory.Services.GetRequiredService<BillingSagaStore>();

    internal static string ProviderOrder(Guid os, string status = "action_required", string detail = "waiting_transfer",
        string? refunds = null, decimal amount = 50, string id = "ORD123") => JsonSerializer.Serialize(new
        {
            id, external_reference = os.ToString("D"), total_amount = amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            status, status_detail = detail,
            transactions = new { refunds = refunds is null ? Array.Empty<object>() : JsonSerializer.Deserialize<object[]>(refunds) }
        });

    internal static HttpRequestMessage Approval(Guid os, string key = "saga-test-key", string email = "test@example.com") =>
        new(HttpMethod.Post, $"/api/billing/ordens/{os}/aprovar")
        {
            Content = JsonContent.Create(new { payerEmail = email }),
            Headers = { { "X-Idempotency-Key", key } }
        };

    internal static HttpRequestMessage Webhook(string id = "ORD123")
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(BillingFactory.Secret),
            Encoding.UTF8.GetBytes($"id:{id};request-id:saga-test;ts:{ts};"))).ToLowerInvariant();
        return new(HttpMethod.Post, $"/api/webhooks/mercado-pago?data.id={id}&type=order")
        {
            Headers = { { "x-request-id", "saga-test" }, { "x-signature", $"ts={ts},v1={signature}" } }
        };
    }

    internal static async Task RegisterAsync(BillingFactory factory, Guid os) =>
        await Service(factory).ConsumeAsync("os.criada", EventBody(os), CancellationToken.None);

    internal static async Task BudgetAsync(BillingFactory factory, Guid os) =>
        await Service(factory).GenerateBudgetAsync(os, 50, CancellationToken.None);

    internal static void MakeDue(BillingFactory factory, Guid os)
    {
        var order = Store(factory).Get(os)!;
        order.NextAttemptAt = DateTimeOffset.MinValue;
        Store(factory).Save(order);
    }

    [Fact]
    public async Task Os_and_deduplication_and_outgoing_event_survive_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "billing-tests", Guid.NewGuid() + ".db");
        var os = Guid.NewGuid();
        var input = EventBody(os);
        Guid outgoingId;
        await using (var first = new BillingFactory { SagaDatabasePath = path })
        {
            await Service(first).ConsumeAsync("os.criada", input, CancellationToken.None);
            await BudgetAsync(first, os);
            await Service(first).RejectAsync(os, CancellationToken.None);
            outgoingId = Assert.Single(Store(first).PendingEvents()).Event.EventId;
        }
        await using var restarted = new BillingFactory { SagaDatabasePath = path };
        await Service(restarted).ConsumeAsync("os.criada", input, CancellationToken.None);
        Assert.Equal("rejected", Service(restarted).Get(os).Decision);
        Assert.Equal(outgoingId, Assert.Single(Store(restarted).PendingEvents()).Event.EventId);
        Assert.Equal(0, restarted.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"eventId\":\"invalid\"}")]
    public async Task Malformed_events_do_not_register_os(string body)
    {
        await using var factory = new BillingFactory();
        await Assert.ThrowsAnyAsync<Exception>(() => Service(factory).ConsumeAsync("os.criada", Encoding.UTF8.GetBytes(body), CancellationToken.None));
        Assert.Empty(Store(factory).PendingEvents());
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Reused_event_id_with_different_os_is_rejected_and_non_utc_is_rejected()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var message = new SagaEvent(eventId, os, DateTimeOffset.UtcNow);
        await Service(factory).ConsumeAsync("os.criada", JsonSerializer.SerializeToUtf8Bytes(message, Json), CancellationToken.None);
        var error = await Assert.ThrowsAsync<BillingRuleException>(() => Service(factory).ConsumeAsync("os.criada",
            JsonSerializer.SerializeToUtf8Bytes(message with { OrdemServicoUniqueCode = Guid.NewGuid() }, Json), CancellationToken.None));
        Assert.Equal(409, error.StatusCode);
        await Assert.ThrowsAsync<BillingRuleException>(() => Service(factory).ConsumeAsync("os.criada",
            JsonSerializer.SerializeToUtf8Bytes(message with { EventId = Guid.NewGuid(), OccurredAt = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(-3)) }, Json), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.123)]
    public async Task Invalid_budget_is_rejected(double amount)
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/billing/ordens/{os}/orcamento", new { amount });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(Service(factory).Get(os).Amount);
    }

    [Fact]
    public async Task Budget_requires_registered_os_and_cannot_change_after_generation()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        using var client = factory.CreateClient();
        using var missing = await client.PostAsJsonAsync($"/api/billing/ordens/{os}/orcamento", new { amount = 50 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        await BudgetAsync(factory, os);
        using var changed = await client.PostAsJsonAsync($"/api/billing/ordens/{os}/orcamento", new { amount = 60 });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(os, Service(factory).Get(os).OrdemServicoUniqueCode);
    }

    [Fact]
    public async Task Explicit_rejection_emits_one_guid_event_without_calling_provider()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        using var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.PostAsync($"/api/billing/ordens/{os}/reprovar", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var outgoing = Assert.Single(Store(factory).PendingEvents());
        Assert.Equal("billing.reprovado", outgoing.RoutingKey);
        Assert.NotEqual(Guid.Empty, outgoing.Event.EventId);
        Assert.Equal(os, outgoing.Event.OrdemServicoUniqueCode);
        Assert.Equal(TimeSpan.Zero, outgoing.Event.OccurredAt.Offset);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Pix_creation_does_not_approve_and_same_key_reuses_payment()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        var postCount = 0;
        factory.Respond = (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postCount++;
                Assert.Equal("saga-test-key", request.Headers.GetValues("X-Idempotency-Key").Single());
            }
            return Task.FromResult(BillingFactory.JsonResponse(ProviderOrder(os)));
        };
        using var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.SendAsync(Approval(os));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, postCount);
        Assert.Empty(Store(factory).PendingEvents());
        Assert.Equal("payment_pending", Service(factory).Get(os).Decision);
        using var changed = await client.SendAsync(Approval(os, email: "other@example.com"));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var rejection = await client.PostAsync($"/api/billing/ordens/{os}/reprovar", null);
        Assert.Equal(HttpStatusCode.Conflict, rejection.StatusCode);
    }

    [Fact]
    public async Task Signed_webhook_confirms_payment_once_and_preserves_legacy_event()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(ProviderOrder(os)));
        using var client = factory.CreateClient();
        using var approval = await client.SendAsync(Approval(os));
        Assert.Empty(Store(factory).PendingEvents());
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(ProviderOrder(os, "processed", "accredited")));
        for (var i = 0; i < 2; i++)
        {
            using var confirmed = await client.SendAsync(Webhook());
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        }
        var outgoing = Assert.Single(Store(factory).PendingEvents());
        Assert.Equal("billing.aprovado", outgoing.RoutingKey);
        Assert.Equal(os, outgoing.Event.OrdemServicoUniqueCode);
        Assert.Equal("approved", Service(factory).Get(os).Decision);
        Assert.Equal(2, factory.Publisher.Events.Count);
    }

    [Theory]
    [InlineData(401, 502)]
    [InlineData(429, 503)]
    [InlineData(500, 502)]
    public async Task Provider_failure_is_not_a_budget_rejection_and_retries_keep_original_key(int providerStatus, int expectedStatus)
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse("{}", (HttpStatusCode)providerStatus));
        using var client = factory.CreateClient();
        using var failed = await client.SendAsync(Approval(os));
        Assert.Equal(expectedStatus, (int)failed.StatusCode);
        Assert.Empty(Store(factory).PendingEvents());
        Assert.Equal("payment_pending", Service(factory).Get(os).Decision);
        using var newKey = await client.SendAsync(Approval(os, "another-key"));
        Assert.Equal(HttpStatusCode.Conflict, newKey.StatusCode);
        factory.Respond = (request, _) =>
        {
            Assert.Equal("saga-test-key", request.Headers.GetValues("X-Idempotency-Key").Single());
            return Task.FromResult(BillingFactory.JsonResponse(ProviderOrder(os)));
        };
        using var retry = await client.SendAsync(Approval(os));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Empty(Store(factory).PendingEvents());
    }

    [Fact]
    public async Task Pending_payment_creation_recovers_after_restart_without_changing_payload_or_key()
    {
        var os = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "billing-tests", Guid.NewGuid() + ".db");
        await using (var first = new BillingFactory { SagaDatabasePath = path })
        {
            await RegisterAsync(first, os);
            await BudgetAsync(first, os);
            first.Respond = (_, _) => throw new TaskCanceledException("provider timeout");
            using var client = first.CreateClient();
            using var timeout = await client.SendAsync(Approval(os));
            Assert.Equal(HttpStatusCode.GatewayTimeout, timeout.StatusCode);
        }
        await using var restarted = new BillingFactory { SagaDatabasePath = path };
        restarted.Respond = async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("saga-test-key", request.Headers.GetValues("X-Idempotency-Key").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(os.ToString("D"), body.RootElement.GetProperty("external_reference").GetString());
            return BillingFactory.JsonResponse(ProviderOrder(os, "processed", "accredited"));
        };
        await Service(restarted).ReconcilePendingAsync(CancellationToken.None);
        Assert.Equal("approved", Service(restarted).Get(os).Decision);
        Assert.Equal("billing.aprovado", Assert.Single(Store(restarted).PendingEvents()).RoutingKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Divergent_payment_does_not_approve(bool wrongReference)
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(
            ProviderOrder(wrongReference ? Guid.NewGuid() : os, "processed", "accredited", amount: wrongReference ? 50 : 60)));
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Approval(os));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(Store(factory).PendingEvents());
    }

    [Fact]
    public async Task Execution_failure_before_os_registration_blocks_late_approval_without_creating_payment()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await Service(factory).ConsumeAsync("execution.falhou", EventBody(os), CancellationToken.None);
        await RegisterAsync(factory, os);
        await Service(factory).ReconcilePendingAsync(CancellationToken.None);
        Assert.Equal("not_applicable", Service(factory).Get(os).CompensationStatus);
        Assert.Equal(0, factory.Requests);
        await Assert.ThrowsAsync<BillingRuleException>(() => BudgetAsync(factory, os));
    }

    [Fact]
    public async Task Refund_is_real_idempotent_and_only_complete_after_confirmed_full_amount()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(ProviderOrder(os, "processed", "accredited")));
        using var client = factory.CreateClient();
        using var approval = await client.SendAsync(Approval(os));
        Assert.Single(Store(factory).PendingEvents());
        var failure = EventBody(os);
        await Service(factory).ConsumeAsync("execution.falhou", failure, CancellationToken.None);
        Assert.Empty(Store(factory).PendingEvents());
        var refunds = 0;
        var submitted = false;
        factory.Respond = (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/v1/orders/ORD123/refund", request.RequestUri!.ToString());
                Assert.Null(request.Content);
                Assert.StartsWith("refund-", request.Headers.GetValues("X-Idempotency-Key").Single());
                refunds++;
                submitted = true;
                return Task.FromResult(BillingFactory.JsonResponse("{}", HttpStatusCode.Created));
            }
            return Task.FromResult(BillingFactory.JsonResponse(submitted
                ? ProviderOrder(os, "refunded", "refunded", """[{"amount":"50.00","status":"processing"}]""")
                : ProviderOrder(os, "processed", "accredited")));
        };
        await Service(factory).ReconcilePendingAsync(CancellationToken.None);
        Assert.Equal(1, refunds);
        Assert.Equal("pending", Service(factory).Get(os).CompensationStatus);
        // Nem mesmo um reembolso parcial com status refunded comprova devolução integral.
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(
            ProviderOrder(os, "refunded", "refunded", """[{"amount":"25.00","status":"processed"}]""")));
        MakeDue(factory, os);
        await Service(factory).ReconcilePendingAsync(CancellationToken.None);
        Assert.Equal("pending", Service(factory).Get(os).CompensationStatus);
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(
            ProviderOrder(os, "refunded", "refunded", """[{"amount":"50.00","status":"processed"}]""")));
        MakeDue(factory, os);
        await Service(factory).ReconcilePendingAsync(CancellationToken.None);
        await Service(factory).ConsumeAsync("execution.falhou", failure, CancellationToken.None);
        Assert.Equal("refunded", Service(factory).Get(os).CompensationStatus);
        Assert.Equal(1, refunds);
        Assert.Empty(Store(factory).PendingEvents());
    }

    [Fact]
    public async Task Refund_timeout_retries_same_operation_and_key_without_false_success()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(ProviderOrder(os, "processed", "accredited")));
        using var client = factory.CreateClient();
        using var approval = await client.SendAsync(Approval(os));
        await Service(factory).ConsumeAsync("execution.falhou", EventBody(os), CancellationToken.None);
        var keys = new List<string>();
        factory.Respond = (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                keys.Add(request.Headers.GetValues("X-Idempotency-Key").Single());
                throw new TaskCanceledException();
            }
            return Task.FromResult(BillingFactory.JsonResponse(ProviderOrder(os, "processed", "accredited")));
        };
        await Service(factory).ReconcilePendingAsync(CancellationToken.None);
        Assert.Equal("pending", Service(factory).Get(os).CompensationStatus);
        MakeDue(factory, os);
        await Service(factory).ReconcilePendingAsync(CancellationToken.None);
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);
        Assert.Empty(Store(factory).PendingEvents());
    }

    [Fact]
    public async Task Payment_key_cannot_be_reused_for_another_os()
    {
        await using var factory = new BillingFactory();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await RegisterAsync(factory, first);
        await RegisterAsync(factory, second);
        await BudgetAsync(factory, first);
        await BudgetAsync(factory, second);
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(ProviderOrder(first)));
        using var client = factory.CreateClient();
        using var initial = await client.SendAsync(Approval(first));
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var requests = factory.Requests;
        using var duplicate = await client.SendAsync(Approval(second));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(requests, factory.Requests);
    }

    [Fact]
    public async Task Failed_reconciliation_stops_at_limit_and_can_be_explicitly_resumed_with_same_key()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse("{}", HttpStatusCode.InternalServerError));
        using var client = factory.CreateClient();
        using var failed = await client.SendAsync(Approval(os));
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var order = Store(factory).Get(os)!;
        order.Failures = 9;
        Store(factory).Save(order);
        await Service(factory).ReconcilePendingAsync(CancellationToken.None);
        Assert.Equal("manual_review", Service(factory).Get(os).CompensationStatus);
        Assert.Empty(Store(factory).PendingOrders());
        using var resumed = await client.PostAsync($"/api/billing/ordens/{os}/reconciliar", null);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Equal("saga-test-key", Store(factory).Get(os)!.PaymentKey);
        Assert.Single(Store(factory).PendingOrders());
    }

    [Fact]
    public async Task Unpaid_order_is_cancelled_and_not_refunded()
    {
        await using var factory = new BillingFactory();
        var os = Guid.NewGuid();
        await RegisterAsync(factory, os);
        await BudgetAsync(factory, os);
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(ProviderOrder(os)));
        using var client = factory.CreateClient();
        using var approval = await client.SendAsync(Approval(os));
        await Service(factory).ConsumeAsync("execution.falhou", EventBody(os), CancellationToken.None);
        var cancelled = false;
        factory.Respond = (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/cancel", request.RequestUri!.ToString());
                cancelled = true;
                return Task.FromResult(BillingFactory.JsonResponse("{}"));
            }
            return Task.FromResult(BillingFactory.JsonResponse(cancelled ? ProviderOrder(os, "canceled", "canceled_transaction") : ProviderOrder(os)));
        };
        await Service(factory).ReconcilePendingAsync(CancellationToken.None);
        Assert.Equal("cancelled", Service(factory).Get(os).CompensationStatus);
        Assert.Empty(Store(factory).PendingEvents());
    }
}
