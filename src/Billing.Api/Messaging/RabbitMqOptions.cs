using System.ComponentModel.DataAnnotations;

namespace Billing.Api.Messaging;

public sealed class RabbitMqOptions
{
    [Required]
    public string HostName { get; init; } = "localhost";
    [Range(1, 65535)]
    public int Port { get; init; } = 5672;
    [Required]
    public string VirtualHost { get; init; } = "/";
    [Required]
    public string UserName { get; init; } = "billing";
    [Required]
    public string Password { get; init; } = "billing-local";
    [Required, MaxLength(200)]
    public string Exchange { get; init; } = "billing.events";
    [Required, MaxLength(200)]
    public string Queue { get; init; } = "billing.payment-status";
    [Range(1, 15)]
    public int PublishTimeoutSeconds { get; init; } = 5;
}
