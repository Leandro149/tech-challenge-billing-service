using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Billing.Api.Messaging;

public sealed class RabbitMqPaymentEventPublisher(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqPaymentEventPublisher> logger) : IPaymentEventPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;
    private IChannel? channel;
    private bool disposed;

    public async Task PublishAsync(PaymentStatusUpdated paymentEvent, CancellationToken ct)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.PublishTimeoutSeconds));
        var acquired = false;
        try
        {
            // Um único publicador utiliza o canal por vez, inclusive durante a reconexão.
            await gate.WaitAsync(timeout.Token);
            acquired = true;
            ObjectDisposedException.ThrowIf(disposed, this);
            if (connection is not { IsOpen: true } || channel is not { IsOpen: true })
            {
                await ResetAsync();
                var factory = new ConnectionFactory
                {
                    HostName = settings.HostName,
                    Port = settings.Port,
                    VirtualHost = settings.VirtualHost,
                    UserName = settings.UserName,
                    Password = settings.Password,
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(settings.PublishTimeoutSeconds),
                    HandshakeContinuationTimeout = TimeSpan.FromSeconds(settings.PublishTimeoutSeconds),
                    ContinuationTimeout = TimeSpan.FromSeconds(settings.PublishTimeoutSeconds),
                    // A próxima entrega recria conexão/canal após falha, inclusive na primeira conexão.
                    AutomaticRecoveryEnabled = false
                };
                connection = await factory.CreateConnectionAsync("billing-api", timeout.Token);
                channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), timeout.Token);
                await channel.ExchangeDeclareAsync(settings.Exchange, ExchangeType.Topic,
                    durable: true, autoDelete: false, cancellationToken: timeout.Token);
                await channel.QueueDeclareAsync(settings.Queue, durable: true, exclusive: false,
                    autoDelete: false, cancellationToken: timeout.Token);
                await channel.QueueBindAsync(settings.Queue, settings.Exchange, PaymentStatusUpdated.EventType,
                    cancellationToken: timeout.Token);
            }

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                ContentEncoding = "utf-8",
                Persistent = true,
                Type = PaymentStatusUpdated.EventType,
                MessageId = paymentEvent.EventId,
                Timestamp = new AmqpTimestamp(paymentEvent.ObservedAt.ToUnixTimeSeconds())
            };
            // Com tracking habilitado, o await aguarda confirmação e falha em nack/basic.return.
            await channel.BasicPublishAsync(settings.Exchange, PaymentStatusUpdated.EventType,
                mandatory: true, basicProperties: properties,
                body: JsonSerializer.SerializeToUtf8Bytes(paymentEvent, Json), cancellationToken: timeout.Token);
        }
        catch (Exception exception) when (exception is RabbitMQClientException or IOException
            or TimeoutException or OperationCanceledException)
        {
            if (acquired) await ResetAsync();
            if (ct.IsCancellationRequested) throw;
            // Não registrar a exceção original: ela pode conter detalhes da conexão.
            logger.LogWarning("Publicação do evento {EventId} não confirmada pelo RabbitMQ ({FailureType}).",
                paymentEvent.EventId, exception.GetType().Name);
            throw new MessagingUnavailableException();
        }
        finally
        {
            if (acquired) gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        if (connection is not null)
        {
            // A biblioteca aplica seu próprio timeout mínimo de encerramento.
            await connection.AbortAsync(TimeSpan.FromSeconds(1));
            if (channel is not null) await channel.DisposeAsync();
            await connection.DisposeAsync();
        }
        channel = null;
        connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            disposed = true;
            await ResetAsync();
        }
        finally
        {
            gate.Release();
        }
    }
}
