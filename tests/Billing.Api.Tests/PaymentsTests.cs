using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Billing.Api.Contracts;

namespace Billing.Api.Tests;

public sealed class PaymentsTests
{
    private static object Input(decimal amount = 50, string email = "test_payer@example.com") =>
        new { orderId = "pedido-123", amount, payerEmail = email, payerFirstName = "APRO" };

    private static HttpRequestMessage Create(object body, string? key = "test-key-123")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/pix") { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("X-Idempotency-Key", key);
        return request;
    }

    [Fact]
    public async Task Create_sends_orders_contract_and_preserves_idempotency_key_on_retries()
    {
        await using var factory = new BillingFactory();
        factory.Respond = async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.mercadopago.com/v1/orders", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(factory.AccessToken, request.Headers.Authorization.Parameter);
            Assert.Equal("test-key-123", request.Headers.GetValues("X-Idempotency-Key").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("automatic", root.GetProperty("processing_mode").GetString());
            Assert.Equal("online", root.GetProperty("type").GetString());
            Assert.Equal("pedido-123", root.GetProperty("external_reference").GetString());
            Assert.Equal("50.00", root.GetProperty("total_amount").GetString());
            Assert.Equal("APRO", root.GetProperty("payer").GetProperty("first_name").GetString());
            var payment = root.GetProperty("transactions").GetProperty("payments")[0];
            Assert.Equal("50.00", payment.GetProperty("amount").GetString());
            Assert.Equal("pix", payment.GetProperty("payment_method").GetProperty("id").GetString());
            Assert.Equal("bank_transfer", payment.GetProperty("payment_method").GetProperty("type").GetString());
            return BillingFactory.JsonResponse(BillingFactory.ProviderOrder);
        };
        using var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.SendAsync(Create(Input()));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.EndsWith("/api/payments/ORD123", response.Headers.Location!.ToString());
            var payment = await response.Content.ReadFromJsonAsync<PaymentResponse>();
            Assert.Equal("pix-copia-cola", payment!.QrCode);
            Assert.Equal("aW1hZ2Vt", payment.QrCodeBase64);
            Assert.Equal("https://example.com/ticket", payment.TicketUrl);
            Assert.Equal(50m, payment.Amount);
            Assert.Equal("action_required", payment.Status);
        }
        Assert.Equal(2, factory.Requests);
    }

    [Theory]
    [InlineData(0, "valid@example.com", "key")]
    [InlineData(-1, "valid@example.com", "key")]
    [InlineData(10.123, "valid@example.com", "key")]
    [InlineData(10, "invalid", "key")]
    [InlineData(10, "valid@example.com", null)]
    [InlineData(10, "valid@example.com", "invalid key")]
    public async Task Invalid_input_is_rejected_before_calling_provider(double amount, string email, string? key)
    {
        await using var factory = new BillingFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Create(Input((decimal)amount, email), key));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Get_fetches_current_state_and_accepts_processing_without_qr_code()
    {
        await using var factory = new BillingFactory();
        factory.Respond = (request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/v1/orders/ORD123", request.RequestUri!.ToString());
            return Task.FromResult(BillingFactory.JsonResponse("""
                { "id": "ORD123", "external_reference": "pedido-123", "total_amount": "50.00", "status": "processing" }
                """));
        };
        using var client = factory.CreateClient();
        var payment = await client.GetFromJsonAsync<PaymentResponse>("/api/payments/ORD123");
        Assert.Equal("processing", payment!.Status);
        Assert.Null(payment.QrCode);
    }

    [Theory]
    [InlineData(400, 422)]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(404, 404)]
    [InlineData(409, 409)]
    [InlineData(429, 503)]
    [InlineData(500, 502)]
    public async Task Provider_errors_return_safe_problem_details(int providerStatus, int expectedStatus)
    {
        await using var factory = new BillingFactory();
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(
            "{\"message\":\"private-provider-detail\"}", (HttpStatusCode)providerStatus));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/payments/ORD123");
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-provider-detail", body);
        Assert.DoesNotContain(factory.AccessToken, body);
    }

    [Fact]
    public async Task Missing_token_returns_configuration_error_without_network_call()
    {
        await using var factory = new BillingFactory { AccessToken = "" };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/payments/ORD123");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Invalid_provider_json_returns_bad_gateway()
    {
        await using var factory = new BillingFactory();
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse("not-json"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/payments/ORD123");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:5173", true)]
    [InlineData("https://untrusted.example", false)]
    public async Task Cors_allows_only_configured_origins(string origin, bool allowed)
    {
        await using var factory = new BillingFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/payments/pix");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-idempotency-key");
        using var response = await client.SendAsync(request);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed) Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
