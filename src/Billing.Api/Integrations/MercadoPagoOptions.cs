namespace Billing.Api.Integrations;

public sealed class MercadoPagoOptions
{
    public string AccessToken { get; init; } = "";
    public string WebhookSecret { get; init; } = "";
}
