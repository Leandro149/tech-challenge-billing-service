using Billing.Api.Saga;
using Microsoft.Extensions.Options;

namespace Billing.Api.Integrations;

public sealed class PaymentOptions
{
    public const string MercadoPago = "MercadoPago";
    public const string Fake = "Fake";
    public string Provider { get; init; } = MercadoPago;

    internal static bool AllowsFake(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");
}

public sealed class PaymentProviders(IServiceProvider services, IOptions<PaymentOptions> options, IHostEnvironment environment)
{
    public string Default => options.Value.Provider;

    public IPaymentProvider Resolve(string name) => name switch
    {
        PaymentOptions.MercadoPago => services.GetRequiredService<MercadoPagoClient>(),
        PaymentOptions.Fake when PaymentOptions.AllowsFake(environment) => services.GetRequiredService<FakePaymentProvider>(),
        _ => throw new IntegrationConfigurationException($"Provedor de pagamento indisponível: {name}.")
    };

    public IPaymentProvider ForPaymentId(string id)
    {
        if (!id.StartsWith(FakePaymentProvider.IdPrefix, StringComparison.Ordinal))
            return Resolve(PaymentOptions.MercadoPago);
        if (!PaymentOptions.AllowsFake(environment))
            throw new BillingRuleException(404, "Cobrança não encontrada.");
        return Resolve(PaymentOptions.Fake);
    }
}
