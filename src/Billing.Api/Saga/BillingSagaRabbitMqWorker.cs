using System.Text.Json;
using Billing.Api.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Billing.Api.Saga;

public sealed class BillingSagaRabbitMqWorker(BillingSagaService saga, BillingSagaStore store,
    IOptions<BillingSagaOptions> sagaOptions, IOptions<RabbitMqOptions> rabbitOptions,
    ILogger<BillingSagaRabbitMqWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = sagaOptions.Value;
        if (!settings.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rabbit = rabbitOptions.Value;
                var factory = new ConnectionFactory
                {
                    HostName = rabbit.HostName, Port = rabbit.Port, VirtualHost = rabbit.VirtualHost,
                    UserName = rabbit.UserName, Password = rabbit.Password, AutomaticRecoveryEnabled = false,
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(rabbit.PublishTimeoutSeconds),
                    HandshakeContinuationTimeout = TimeSpan.FromSeconds(rabbit.PublishTimeoutSeconds),
                    ContinuationTimeout = TimeSpan.FromSeconds(rabbit.PublishTimeoutSeconds)
                };
                await using var connection = await factory.CreateConnectionAsync("billing-saga", stoppingToken);
                await using var consumerChannel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await DeclareTopologyAsync(consumerChannel, settings, stoppingToken);
                await consumerChannel.BasicQosAsync(0, 1, false, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(consumerChannel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        await saga.ConsumeAsync(delivery.RoutingKey, delivery.Body, stoppingToken);
                        await consumerChannel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Payload inválido/conflito/falha de persistência vai para a DLQ, sem ACK falso.
                        logger.LogWarning("Evento {RoutingKey} enviado à fila de erro ({FailureType}).",
                            delivery.RoutingKey, exception.GetType().Name);
                        await consumerChannel.BasicNackAsync(delivery.DeliveryTag, false, false, stoppingToken);
                    }
                };
                await consumerChannel.BasicConsumeAsync(settings.Queue, autoAck: false, consumer, stoppingToken);
                await using var publisherChannel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                while (connection.IsOpen && consumerChannel.IsOpen && publisherChannel.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    foreach (var outgoing in store.PendingEvents())
                    {
                        try
                        {
                            // Uma falha de execução impede publicação tardia de aprovação ainda pendente.
                            if (outgoing.RoutingKey == "billing.aprovado" &&
                                store.Get(outgoing.Event.OrdemServicoUniqueCode)?.CompensationRequested == true)
                            {
                                store.MarkSent(outgoing.Event.EventId);
                                continue;
                            }
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            timeout.CancelAfter(TimeSpan.FromSeconds(rabbit.PublishTimeoutSeconds));
                            await publisherChannel.BasicPublishAsync(settings.Exchange, outgoing.RoutingKey,
                                mandatory: true, basicProperties: new BasicProperties
                                {
                                    Persistent = true, ContentType = "application/json", ContentEncoding = "utf-8",
                                    Type = outgoing.RoutingKey, MessageId = outgoing.Event.EventId.ToString("D"),
                                    Timestamp = new AmqpTimestamp(outgoing.Event.OccurredAt.ToUnixTimeSeconds())
                                }, body: JsonSerializer.SerializeToUtf8Bytes(outgoing.Event, Json), cancellationToken: timeout.Token);
                            store.MarkSent(outgoing.Event.EventId);
                        }
                        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                        {
                            logger.LogWarning("Evento {EventId} permanece pendente de publicação ({FailureType}).",
                                outgoing.Event.EventId, exception.GetType().Name);
                            break;
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Conexão RabbitMQ da Saga indisponível ({FailureType}); nova tentativa em breve.", exception.GetType().Name);
            }
            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), stoppingToken);
        }
    }

    public static async Task DeclareTopologyAsync(IChannel channel, BillingSagaOptions settings, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(settings.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        var errors = settings.Exchange + ".errors";
        await channel.ExchangeDeclareAsync(errors, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(settings.Queue + ".error", durable: true, exclusive: false,
            autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(settings.Queue + ".error", errors, settings.Queue, cancellationToken: ct);
        await channel.QueueDeclareAsync(settings.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = errors, ["x-dead-letter-routing-key"] = settings.Queue
            }, cancellationToken: ct);
        await channel.QueueBindAsync(settings.Queue, settings.Exchange, "os.criada", cancellationToken: ct);
        await channel.QueueBindAsync(settings.Queue, settings.Exchange, "execution.falhou", cancellationToken: ct);
    }
}

public sealed class BillingSagaReconciliationWorker(BillingSagaService saga, IOptions<BillingSagaOptions> options,
    ILogger<BillingSagaReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await saga.ReconcilePendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Reconciliação do Billing indisponível ({FailureType}).", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken);
        }
    }
}
