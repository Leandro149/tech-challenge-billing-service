using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Billing.Api.Integrations;
using Billing.Api.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Billing.Api.Tests;

public sealed class BillingFactory : WebApplicationFactory<Program>
{
    public const string Secret = "webhook-secret-for-tests-only";
    public const string ProviderOrder = """
        {
          "id": "ORD123", "external_reference": "pedido-123", "total_amount": "50.00",
          "status": "action_required", "status_detail": "waiting_transfer",
          "transactions": { "payments": [{ "payment_method": {
            "qr_code": "pix-copia-cola", "qr_code_base64": "aW1hZ2Vt", "ticket_url": "https://example.com/ticket"
          }}] }
        }
        """;

    public string AccessToken { get; set; } = "fake-token-for-tests-only";
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(JsonResponse(ProviderOrder));
    public int Requests { get; private set; }
    public RecordingPublisher Publisher { get; } = new();
    public bool UseRabbitMq { get; init; }
    public bool SagaEnabled { get; init; }
    public string? PaymentProvider { get; init; }
    public string EnvironmentName { get; init; } = "Development";
    public string? SagaDatabasePath { get; init; }
    public string RabbitMqResource { get; } = $"billing.test.{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MercadoPago:AccessToken"] = AccessToken,
            ["MercadoPago:WebhookSecret"] = Secret,
            ["Saga:Enabled"] = SagaEnabled.ToString(),
            ["Saga:DatabasePath"] = SagaDatabasePath ?? Path.Combine(Path.GetTempPath(), "billing-tests", RabbitMqResource + ".db"),
            ["Saga:Exchange"] = RabbitMqResource + ".saga",
            ["Saga:Queue"] = RabbitMqResource + ".saga",
            ["Saga:PollSeconds"] = "1",
            ["RabbitMq:HostName"] = "localhost",
            ["RabbitMq:Port"] = "5672",
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:UserName"] = "billing",
            ["RabbitMq:Password"] = "billing-local",
            ["RabbitMq:Exchange"] = RabbitMqResource,
            ["RabbitMq:Queue"] = RabbitMqResource
        }));
        if (PaymentProvider is not null)
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Payment:Provider"] = PaymentProvider }));
        builder.ConfigureServices(services =>
        {
            if (!UseRabbitMq)
            {
                services.RemoveAll<IPaymentEventPublisher>();
                services.AddSingleton<IPaymentEventPublisher>(Publisher);
            }
            services.AddHttpClient<MercadoPagoClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async (request, ct) =>
                {
                    Requests++;
                    return await Respond(request, ct);
                }));
        });
    }

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    public sealed class RecordingPublisher : IPaymentEventPublisher
    {
        public ConcurrentQueue<PaymentStatusUpdated> Events { get; } = new();
        public Func<PaymentStatusUpdated, CancellationToken, Task> OnPublish { get; set; } = (_, _) => Task.CompletedTask;

        public async Task PublishAsync(PaymentStatusUpdated paymentEvent, CancellationToken ct)
        {
            await OnPublish(paymentEvent, ct);
            Events.Enqueue(paymentEvent);
        }
    }
}
