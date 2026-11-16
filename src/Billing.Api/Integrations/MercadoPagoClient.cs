using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Billing.Api.Contracts;
using Microsoft.Extensions.Options;

namespace Billing.Api.Integrations;

public sealed class MercadoPagoClient(HttpClient http, IOptions<MercadoPagoOptions> options)
{
    private static readonly JsonSerializerOptions ProviderJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<PaymentResponse> CreatePixAsync(CreatePixRequest input, string idempotencyKey, CancellationToken ct)
    {
        var amount = input.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var payload = new
        {
            Type = "online",
            ProcessingMode = "automatic",
            TotalAmount = amount,
            ExternalReference = input.OrderId,
            Payer = new { Email = input.PayerEmail, FirstName = input.PayerFirstName },
            Transactions = new
            {
                Payments = new[] { new { Amount = amount, PaymentMethod = new { Id = "pix", Type = "bank_transfer" } } }
            }
        };
        return SendAsync(HttpMethod.Post, "v1/orders", payload, idempotencyKey, ct);
    }

    public Task<PaymentResponse> GetAsync(string orderId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"v1/orders/{Uri.EscapeDataString(orderId)}", null, null, ct);

    private async Task<PaymentResponse> SendAsync(HttpMethod method, string path, object? payload, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.AccessToken))
            throw new IntegrationConfigurationException("Configure MercadoPago:AccessToken nos User Secrets ou no ambiente.");

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AccessToken);
        if (key is not null)
            request.Headers.Add("X-Idempotency-Key", key);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: ProviderJson);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new MercadoPagoException(response.StatusCode);

        MercadoPagoOrder? order;
        try
        {
            order = await response.Content.ReadFromJsonAsync<MercadoPagoOrder>(ProviderJson, ct);
        }
        catch (JsonException)
        {
            throw new MercadoPagoException(HttpStatusCode.BadGateway);
        }

        if (order is null || string.IsNullOrEmpty(order.Id) || string.IsNullOrEmpty(order.Status) ||
            !decimal.TryParse(order.TotalAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            throw new MercadoPagoException(HttpStatusCode.BadGateway);

        var pix = order.Transactions?.Payments?.FirstOrDefault()?.PaymentMethod;
        return new PaymentResponse(order.Id, order.ExternalReference, amount, order.Status, order.StatusDetail,
            pix?.QrCode, pix?.QrCodeBase64, pix?.TicketUrl);
    }
}

public sealed class MercadoPagoException(HttpStatusCode statusCode) : Exception("Falha na operação com o Mercado Pago.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class IntegrationConfigurationException(string message) : Exception(message);
