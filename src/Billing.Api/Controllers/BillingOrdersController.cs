using System.ComponentModel.DataAnnotations;
using Billing.Api.Contracts;
using Billing.Api.Saga;
using Microsoft.AspNetCore.Mvc;

namespace Billing.Api.Controllers;

[ApiController]
[Route("api/billing/ordens/{ordemServicoUniqueCode:guid}")]
public sealed class BillingOrdersController(BillingSagaService saga) : ControllerBase
{
    [HttpGet]
    public ActionResult<BillingOrderResponse> Get(Guid ordemServicoUniqueCode) => Ok(saga.Get(ordemServicoUniqueCode));

    [HttpPost("orcamento")]
    public async Task<ActionResult<BillingOrderResponse>> GenerateBudget(Guid ordemServicoUniqueCode,
        GenerateBudgetRequest request, CancellationToken ct) =>
        Ok(await saga.GenerateBudgetAsync(ordemServicoUniqueCode, request.Amount, ct));

    [HttpPost("aprovar")]
    public async Task<ActionResult<PaymentResponse>> Approve(Guid ordemServicoUniqueCode, ApproveBudgetRequest request,
        [FromHeader(Name = "X-Idempotency-Key"), Required, RegularExpression(@"^[a-zA-Z0-9_.-]{1,128}$")] string key,
        CancellationToken ct) => Ok(await saga.ApproveAsync(ordemServicoUniqueCode, request, key, ct));

    [HttpPost("reprovar")]
    public async Task<ActionResult<BillingOrderResponse>> Reject(Guid ordemServicoUniqueCode, CancellationToken ct) =>
        Ok(await saga.RejectAsync(ordemServicoUniqueCode, ct));

    [HttpPost("reconciliar")]
    public async Task<ActionResult<BillingOrderResponse>> Reconcile(Guid ordemServicoUniqueCode, CancellationToken ct) =>
        Accepted(await saga.RetryReconciliationAsync(ordemServicoUniqueCode, ct));
}
