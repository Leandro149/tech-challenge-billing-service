using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private const int MaxErrorBodyLength = 2000;
    private static readonly string[] SensitiveKeys =
    [
        "token", "authorization", "secret", "password", "email", "identification",
        "document", "phone", "card", "qr_code", "first_name", "last_name"
    ];

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
        => Map(await SendOrderAsync(method, path, payload, key, ct));

    public async Task<MercadoPagoFinancialState> GetFinancialStateAsync(string orderId, CancellationToken ct)
    {
        var order = await SendOrderAsync(HttpMethod.Get, $"v1/orders/{Uri.EscapeDataString(orderId)}", null, null, ct);
        var payment = Map(order);
        var refunds = order.Transactions?.Refunds;
        var amounts = refunds?.Select(r => decimal.TryParse(r.Amount, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) ? value : -1m).ToArray();
        var confirmed = order.StatusDetail == "refunded" && (order.Status is "processed" or "refunded") &&
            refunds is { Count: > 0 } && refunds.All(r => r.Status == "processed") &&
            amounts is { Length: > 0 } && amounts.All(v => v > 0) && amounts.Sum() == payment.Amount;
        return new(payment, confirmed, refunds?.Any(r => r.Status == "processing") == true);
    }

    public Task RefundAsync(string orderId, string key, CancellationToken ct) =>
        ExecuteOperationAsync(orderId, "refund", key, ct);

    public Task CancelAsync(string orderId, string key, CancellationToken ct) =>
        ExecuteOperationAsync(orderId, "cancel", key, ct);

    private async Task ExecuteOperationAsync(string orderId, string operation, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.AccessToken))
            throw new IntegrationConfigurationException("Configure MercadoPago:AccessToken nos User Secrets ou no ambiente.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"v1/orders/{Uri.EscapeDataString(orderId)}/{operation}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AccessToken);
        request.Headers.Add("X-Idempotency-Key", key);
        // Devolução total e cancelamento da Orders API usam POST sem body.
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new MercadoPagoException(response.StatusCode, await ReadSafeErrorBodyAsync(response, ct));
        // Sucesso HTTP aceita a solicitação; a confirmação é consultada separadamente.
    }

    private static async Task<string?> ReadSafeErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var node = JsonNode.Parse(raw);
            Redact(node);
            var text = node?.ToJsonString();
            return text is { Length: > MaxErrorBodyLength } ? text[..MaxErrorBodyLength] + "…" : text;
        }
        catch (JsonException)
        {
            return "<corpo não JSON omitido>";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return null;
        }
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (SensitiveKeys.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase)))
                        obj[key] = "***";
                    else
                        Redact(obj[key]);
                }
                break;
            case JsonArray array:
                foreach (var item in array) Redact(item);
                break;
        }
    }

    private async Task<MercadoPagoOrder> SendOrderAsync(HttpMethod method, string path, object? payload, string? key, CancellationToken ct)
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

        if (order is null) throw new MercadoPagoException(HttpStatusCode.BadGateway);
        return order;
    }

    private static PaymentResponse Map(MercadoPagoOrder order)
    {
        if (string.IsNullOrEmpty(order.Id) || string.IsNullOrEmpty(order.Status) ||
            !decimal.TryParse(order.TotalAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            throw new MercadoPagoException(HttpStatusCode.BadGateway);

        var pix = order.Transactions?.Payments?.FirstOrDefault()?.PaymentMethod;
        return new PaymentResponse(order.Id, order.ExternalReference, amount, order.Status, order.StatusDetail,
            pix?.QrCode, pix?.QrCodeBase64, pix?.TicketUrl);
    }
}

public sealed class MercadoPagoException(HttpStatusCode statusCode, string? providerBody = null)
    : Exception("Falha na operação com o Mercado Pago.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    // Corpo JSON do provedor, com campos sensíveis mascarados e tamanho limitado.
    public string? ProviderBody { get; } = providerBody;
}

public sealed class IntegrationConfigurationException(string message) : Exception(message);

public sealed record MercadoPagoFinancialState(PaymentResponse Payment, bool RefundConfirmed, bool RefundInProgress);
