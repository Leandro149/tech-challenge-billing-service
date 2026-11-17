using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Billing.Api.Contracts;
using Billing.Api.Saga;
using RabbitMQ.Client;

namespace Billing.Api.Tests;

public sealed class BillingSagaRabbitMqTests
{
    [RabbitMqFact]
    [Trait("Category", "RabbitMq")]
    public Task Choreography_consumes_os_publishes_only_paid_approval_compensates_and_dead_letters_poison() =>
        RunChoreographyAsync(useFake: false);

    [RabbitMqFact]
    [Trait("Category", "RabbitMq")]
    public Task Fake_payment_choreography_publishes_approval_and_confirms_refund_without_mercado_pago() =>
        RunChoreographyAsync(useFake: true);

    private static async Task RunChoreographyAsync(bool useFake)
    {
        var factory = new BillingFactory { SagaEnabled = true, PaymentProvider = useFake ? "Fake" : null };
        var settings = new BillingSagaOptions
        {
            Exchange = factory.RabbitMqResource + ".saga", Queue = factory.RabbitMqResource + ".saga"
        };
        var rabbit = new ConnectionFactory { UserName = "billing", Password = "billing-local" };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = await rabbit.CreateConnectionAsync(deadline.Token);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), deadline.Token);
        await BillingSagaRabbitMqWorker.DeclareTopologyAsync(channel, settings, deadline.Token);
        var outputQueue = settings.Queue + ".test-output";
        await channel.QueueDeclareAsync(outputQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: deadline.Token);
        await channel.QueueBindAsync(outputQueue, settings.Exchange, "billing.#", cancellationToken: deadline.Token);
        var os = Guid.NewGuid();
        var phase = 0;
        var refundRequests = 0;
        factory.Respond = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/refund"))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Interlocked.Increment(ref refundRequests);
                Volatile.Write(ref phase, 2);
                return Task.FromResult(BillingFactory.JsonResponse("{}", HttpStatusCode.Created));
            }
            var current = Volatile.Read(ref phase);
            return Task.FromResult(BillingFactory.JsonResponse(current switch
            {
                0 => BillingSagaTests.ProviderOrder(os),
                1 => BillingSagaTests.ProviderOrder(os, "processed", "accredited"),
                _ => BillingSagaTests.ProviderOrder(os, "refunded", "refunded", """[{"amount":"50.00","status":"processed"}]""")
            }));
        };
        try
        {
            using var client = factory.CreateClient();
            var created = BillingSagaTests.EventBody(os);
            await Publish("os.criada", created);
            await WaitUntilAsync(() => Task.FromResult(BillingSagaTests.Store(factory).Get(os)?.Registered == true), deadline.Token);
            using var budget = await client.PostAsJsonAsync($"/api/billing/ordens/{os}/orcamento", new { amount = 50 }, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, budget.StatusCode);
            await Publish("os.criada", created); // Redelivery não pode apagar orçamento/decisão.
            using var payment = await client.SendAsync(BillingSagaTests.Approval(os), deadline.Token);
            Assert.Equal(HttpStatusCode.OK, payment.StatusCode);
            var paymentId = (await payment.Content.ReadFromJsonAsync<PaymentResponse>(deadline.Token))!.Id;
            if (!useFake)
            {
                Assert.Null(await channel.BasicGetAsync(outputQueue, true, deadline.Token));
                Volatile.Write(ref phase, 1);
                using var webhook = await client.SendAsync(BillingSagaTests.Webhook(), deadline.Token);
                Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
            }
            BasicGetResult? approved = null;
            await WaitUntilAsync(async () => (approved = await channel.BasicGetAsync(outputQueue, true, deadline.Token)) is not null, deadline.Token);
            Assert.Equal("billing.aprovado", approved!.RoutingKey);
            var payload = JsonSerializer.Deserialize<SagaEvent>(approved.Body.Span, BillingSagaTests.Json)!;
            Assert.Equal(os, payload.OrdemServicoUniqueCode);
            Assert.Equal(payload.EventId.ToString("D"), approved.BasicProperties.MessageId);
            Assert.Equal(TimeSpan.Zero, payload.OccurredAt.Offset);
            Assert.True(approved.BasicProperties.Persistent);

            var failed = BillingSagaTests.EventBody(os);
            await Publish("execution.falhou", failed);
            await Publish("execution.falhou", failed);
            await WaitUntilAsync(() => Task.FromResult(BillingSagaTests.Store(factory).Get(os)?.CompensationStatus == "refunded"), deadline.Token);
            Assert.Equal(useFake ? 0 : 1, refundRequests);
            if (useFake)
            {
                var refunded = await client.GetFromJsonAsync<PaymentResponse>($"/api/payments/{paymentId}", deadline.Token);
                Assert.Equal("refunded", refunded!.Status);
                Assert.Equal(0, factory.Requests);
            }

            var rejectedOs = Guid.NewGuid();
            await Publish("os.criada", BillingSagaTests.EventBody(rejectedOs));
            await WaitUntilAsync(() => Task.FromResult(BillingSagaTests.Store(factory).Get(rejectedOs)?.Registered == true), deadline.Token);
            await BillingSagaTests.BudgetAsync(factory, rejectedOs);
            using var rejected = await client.PostAsync($"/api/billing/ordens/{rejectedOs}/reprovar", null, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            BasicGetResult? rejection = null;
            await WaitUntilAsync(async () => (rejection = await channel.BasicGetAsync(outputQueue, true, deadline.Token)) is not null, deadline.Token);
            Assert.Equal("billing.reprovado", rejection!.RoutingKey);

            await Publish("os.criada", Encoding.UTF8.GetBytes("{}"));
            BasicGetResult? poison = null;
            await WaitUntilAsync(async () => (poison = await channel.BasicGetAsync(settings.Queue + ".error", true, deadline.Token)) is not null, deadline.Token);
            Assert.Equal("{}", Encoding.UTF8.GetString(poison!.Body.Span));
            Assert.True(poison.BasicProperties.Headers!.ContainsKey("x-death"));
        }
        finally
        {
            await factory.DisposeAsync();
            await channel.QueueDeleteAsync(settings.Queue, cancellationToken: deadline.Token);
            await channel.QueueDeleteAsync(settings.Queue + ".error", cancellationToken: deadline.Token);
            await channel.QueueDeleteAsync(outputQueue, cancellationToken: deadline.Token);
            await channel.ExchangeDeleteAsync(settings.Exchange, cancellationToken: deadline.Token);
            await channel.ExchangeDeleteAsync(settings.Exchange + ".errors", cancellationToken: deadline.Token);
        }

        async Task Publish(string key, byte[] body) => await channel.BasicPublishAsync(settings.Exchange, key,
            true, new BasicProperties { Persistent = true, ContentType = "application/json" }, body, deadline.Token);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        while (!await condition()) await Task.Delay(100, ct);
    }
}
