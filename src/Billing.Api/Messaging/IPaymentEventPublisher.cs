namespace Billing.Api.Messaging;

public interface IPaymentEventPublisher
{
    Task PublishAsync(PaymentStatusUpdated paymentEvent, CancellationToken ct);
}

public sealed class MessagingUnavailableException() : Exception(
    "Não foi possível confirmar a publicação no RabbitMQ. A notificação pode ser reenviada.");
