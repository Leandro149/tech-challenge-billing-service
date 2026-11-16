using System.ComponentModel.DataAnnotations;
using Billing.Api.Contracts;
using Billing.Api.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace Billing.Api.Controllers;

[ApiController]
[Route("api/payments")]
public sealed class PaymentsController(MercadoPagoClient mercadoPago) : ControllerBase
{
    [HttpPost("pix")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentResponse>> CreatePix(
        [FromBody] CreatePixRequest input,
        [FromHeader(Name = "X-Idempotency-Key"), Required, RegularExpression(@"^[a-zA-Z0-9_.-]{1,128}$")] string idempotencyKey,
        CancellationToken ct)
    {
        var payment = await mercadoPago.CreatePixAsync(input, idempotencyKey, ct);
        return CreatedAtAction(nameof(Get), new { orderId = payment.Id }, payment);
    }

    [HttpGet("{orderId}")]
    [ProducesResponseType<PaymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentResponse>> Get(
        [FromRoute, RegularExpression(@"^ORD[a-zA-Z0-9]{1,61}$")] string orderId, CancellationToken ct) =>
        Ok(await mercadoPago.GetAsync(orderId, ct));
}
