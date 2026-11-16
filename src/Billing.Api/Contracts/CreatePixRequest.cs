using System.ComponentModel.DataAnnotations;

namespace Billing.Api.Contracts;

public sealed class CreatePixRequest : IValidatableObject
{
    [Required, RegularExpression(@"^[a-zA-Z0-9_-]{1,64}$")]
    public string OrderId { get; init; } = "";

    [Range(typeof(decimal), "0.01", "999999999.99", ParseLimitsInInvariantCulture = true)]
    public decimal Amount { get; init; }

    [Required, EmailAddress, MaxLength(254)]
    public string PayerEmail { get; init; } = "";

    [MaxLength(100)]
    public string? PayerFirstName { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (decimal.Round(Amount, 2) != Amount)
            yield return new ValidationResult("O valor deve ter no máximo duas casas decimais.", [nameof(Amount)]);
    }
}
