using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Billing.Api.Contracts;
using Billing.Api.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Billing.Api.Tests;

public sealed class RabbitMqPublisherTests
{
    private static readonly PaymentResponse Payment = new(
        "ORD123", "pedido-123", 50m, "processed", "accredited", "private-pix", "private-image", "private-ticket");

    [Fact]
    public void Event_identifies_same_state_across_redeliveries_and_omits_pix_data()
    {
        var first = PaymentStatusUpdated.From(Payment);
        var duplicate = PaymentStatusUpdated.From(Payment);
        var updated = PaymentStatusUpdated.From(Payment with { Status = "refunded" });
        var detailChanged = PaymentStatusUpdated.From(Payment with { StatusDetail = null });
        var anotherOrder = PaymentStatusUpdated.From(Payment with { Id = "ORD456" });

        Assert.Equal(first.EventId, duplicate.EventId);
        Assert.NotEqual(first.EventId, updated.EventId);
        Assert.NotEqual(first.EventId, detailChanged.EventId);
        Assert.NotEqual(first.EventId, anotherOrder.EventId);
        Assert.Equal("ORD123", first.PaymentId);
        Assert.Equal("pedido-123", first.OrderId);
        Assert.Equal(50m, first.Amount);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(first));
    }

    [Fact]
    public async Task Nonresponding_broker_times_out_safely_and_allows_next_attempt()
    {
        // TCP aceita conexões, mas não responde ao handshake AMQP: não depende de RabbitMQ externo.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = new RabbitMqOptions
        {
            HostName = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Password = "private-broker-password",
            PublishTimeoutSeconds = 1
        };
        await using var publisher = CreatePublisher(settings);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var elapsed = Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<MessagingUnavailableException>(() =>
                publisher.PublishAsync(PaymentStatusUpdated.From(Payment), CancellationToken.None));
            // O cliente pode aguardar o encerramento interno depois do timeout da operação.
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(settings.PublishTimeoutSeconds + 10));
            Assert.DoesNotContain(settings.Password, exception.ToString());
            Assert.Null(exception.InnerException);
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        await using var publisher = CreatePublisher(new RabbitMqOptions());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            publisher.PublishAsync(PaymentStatusUpdated.From(Payment), cancelled.Token));
    }

    [RabbitMqFact]
    [Trait("Category", "RabbitMq")]
    public async Task Real_broker_routes_persistent_json_rejects_unroutable_and_recovers()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var settings = new RabbitMqOptions
        {
            Exchange = $"billing.test.{suffix}",
            Queue = $"billing.test.{suffix}"
        };
        var factory = new ConnectionFactory
        {
            HostName = settings.HostName, Port = settings.Port, VirtualHost = settings.VirtualHost,
            UserName = settings.UserName, Password = settings.Password
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await factory.CreateConnectionAsync(deadline.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: deadline.Token);
        await using var publisher = CreatePublisher(settings);
        var paymentEvent = PaymentStatusUpdated.From(Payment);
        try
        {
            await publisher.PublishAsync(paymentEvent, deadline.Token);
            var message = await channel.BasicGetAsync(settings.Queue, autoAck: true, deadline.Token);
            Assert.NotNull(message);
            Assert.Equal(paymentEvent.EventId, message.BasicProperties.MessageId);
            Assert.True(message.BasicProperties.Persistent);
            Assert.Equal("application/json", message.BasicProperties.ContentType);
            Assert.Equal(PaymentStatusUpdated.EventType, message.BasicProperties.Type);
            Assert.Equal(PaymentStatusUpdated.EventType, message.RoutingKey);
            using var body = JsonDocument.Parse(message.Body);
            Assert.Equal(paymentEvent.EventId, body.RootElement.GetProperty("eventId").GetString());
            Assert.Equal("ORD123", body.RootElement.GetProperty("paymentId").GetString());
            Assert.Equal("accredited", body.RootElement.GetProperty("statusDetail").GetString());
            Assert.DoesNotContain("private-", body.RootElement.GetRawText());

            // Remover o binding comprova que mandatory + confirms não aceitam perda silenciosa.
            await channel.QueueUnbindAsync(settings.Queue, settings.Exchange, PaymentStatusUpdated.EventType,
                cancellationToken: deadline.Token);
            await Assert.ThrowsAsync<MessagingUnavailableException>(() => publisher.PublishAsync(paymentEvent, deadline.Token));

            // Após falha o publicador recria conexão, canal e binding automaticamente.
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => publisher.PublishAsync(paymentEvent, deadline.Token)));
            for (var i = 0; i < 5; i++)
            {
                var retry = await channel.BasicGetAsync(settings.Queue, autoAck: true, deadline.Token);
                Assert.NotNull(retry);
                Assert.Equal(paymentEvent.EventId, retry.BasicProperties.MessageId);
            }
            Assert.Null(await channel.BasicGetAsync(settings.Queue, autoAck: true, deadline.Token));
        }
        finally
        {
            // Apenas recursos exclusivos deste teste são removidos; não acessa a fila de negócio.
            await channel.QueueDeleteAsync(settings.Queue, cancellationToken: deadline.Token);
            await channel.ExchangeDeleteAsync(settings.Exchange, cancellationToken: deadline.Token);
        }
    }

    private static RabbitMqPaymentEventPublisher CreatePublisher(RabbitMqOptions settings) =>
        new(Options.Create(settings), NullLogger<RabbitMqPaymentEventPublisher>.Instance);
}

public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BILLING_TEST_RABBITMQ") != "true")
            Skip = "Inicie o Compose e defina BILLING_TEST_RABBITMQ=true para testar o broker real.";
    }
}
