using System.ComponentModel.DataAnnotations;

namespace Billing.Api.Saga;

public sealed record SagaEvent(Guid EventId, Guid OrdemServicoUniqueCode, DateTimeOffset OccurredAt);
public sealed record SagaOutgoingEvent(string RoutingKey, SagaEvent Event);

public sealed class BillingSagaOptions
{
    public bool Enabled { get; init; } = true;
    [Required] public string Exchange { get; init; } = "techchallenge.events";
    [Required] public string Queue { get; init; } = "billing-service.events";
    [Required] public string DatabasePath { get; init; } = ".data/billing.db";
    [Range(1, 300)] public int PollSeconds { get; init; } = 5;
    [Range(1, 100)] public int MaxFailures { get; init; } = 10;
}

public sealed class GenerateBudgetRequest : IValidatableObject
{
    [Range(typeof(decimal), "0.01", "999999999.99", ParseLimitsInInvariantCulture = true)]
    public decimal Amount { get; init; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (decimal.Round(Amount, 2) != Amount)
            yield return new("O orçamento deve ter no máximo duas casas decimais.", [nameof(Amount)]);
    }
}

public sealed class ApproveBudgetRequest
{
    [Required, EmailAddress, MaxLength(254)] public string PayerEmail { get; init; } = "";
    [MaxLength(100)] public string? PayerFirstName { get; init; }
}

public sealed record BillingOrderResponse(Guid OrdemServicoUniqueCode, decimal? Amount, string Decision,
    string? PaymentId, bool CompensationRequested, string CompensationStatus, string? LastError);

// Apenas dados necessários à recuperação; credenciais do provedor não são persistidas.
public sealed class BillingOrder
{
    public Guid OrdemServicoUniqueCode { get; init; }
    public bool Registered { get; set; }
    public decimal? Amount { get; set; }
    public string Decision { get; set; } = "pending";
    public string? PaymentId { get; set; }
    public string? PaymentKey { get; set; }
    public string? PayerEmail { get; set; }
    public string? PayerFirstName { get; set; }
    public bool PaymentCreationPending { get; set; }
    public Guid? ApprovalEventId { get; set; }
    public bool CompensationRequested { get; set; }
    public string CompensationStatus { get; set; } = "none";
    public string? CompensationAction { get; set; }
    public bool CompensationSubmitted { get; set; }
    public Guid CompensationKey { get; set; } = Guid.NewGuid();
    public int Failures { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    public BillingOrderResponse ToResponse() => new(OrdemServicoUniqueCode, Amount, Decision, PaymentId,
        CompensationRequested, CompensationStatus, LastError);
}

public sealed class BillingRuleException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
