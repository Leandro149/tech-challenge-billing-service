using System.Text.RegularExpressions;
using Billing.Api.Integrations;
using Billing.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Billing.Api.Controllers;

[ApiController]
[Route("api/webhooks/mercado-pago")]
public sealed class MercadoPagoWebhookController(
    MercadoPagoClient mercadoPago,
    IOptions<MercadoPagoOptions> options,
    IPaymentEventPublisher events,
    ILogger<MercadoPagoWebhookController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.WebhookSecret))
            throw new IntegrationConfigurationException("Configure MercadoPago:WebhookSecret para receber notificações.");

        // O ID consultado é o mesmo ID assinado da query, nunca o ID de um body não assinado.
        var dataId = Request.Query["data.id"].ToString();
        if (Request.Query["data.id"].Count != 1 ||
            Request.Headers["x-signature"].Count != 1 ||
            Request.Headers["x-request-id"].Count > 1 ||
            !WebhookSignatureValidator.IsValid(Request.Headers["x-signature"].ToString(),
                Request.Headers["x-request-id"].ToString(), dataId, options.Value.WebhookSecret))
            return Unauthorized();

        if (Request.Query["type"] != "order") return Ok(new { received = true });
        if (!Regex.IsMatch(dataId, @"^ORD[a-zA-Z0-9]{1,61}$")) return BadRequest();

        var payment = await mercadoPago.GetAsync(dataId, ct);
        await events.PublishAsync(PaymentStatusUpdated.From(payment), ct);
        logger.LogInformation("Notificação da order {OrderId}: {Status} / {StatusDetail}",
            payment.Id, payment.Status, payment.StatusDetail);
        return Ok(new { received = true });
    }
}
