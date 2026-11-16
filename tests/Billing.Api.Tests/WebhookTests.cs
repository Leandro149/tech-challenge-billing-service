using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Billing.Api.Tests;

public sealed class WebhookTests
{
    private static HttpRequestMessage Notification(
        string signedId = "ORD123", string queryId = "ORD123", string? hash = null, long? timestamp = null)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var digest = hash ?? Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(BillingFactory.Secret),
            Encoding.UTF8.GetBytes($"id:{signedId};request-id:test-request;ts:{ts};"))).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/mercado-pago?data.id={queryId}&type=order")
        {
            // O body não deve substituir o ID efetivamente coberto pela assinatura.
            Content = new StringContent("{\"data\":{\"id\":\"ORD999\"}}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-request-id", "test-request");
        request.Headers.Add("x-signature", $"ts={ts},v1={digest}");
        return request;
    }

    [Fact]
    public async Task Valid_notification_fetches_signed_query_id_and_accepts_duplicates()
    {
        await using var factory = new BillingFactory();
        factory.Respond = (request, _) =>
        {
            Assert.EndsWith("/orders/ORD123", request.RequestUri!.ToString());
            return Task.FromResult(BillingFactory.JsonResponse(BillingFactory.ProviderOrder));
        };
        using var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.SendAsync(Notification());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(2, factory.Requests);
    }

    [Theory]
    [InlineData("ORD123", "ORD456", null)]
    [InlineData("ORD123", "ORD123", "invalid")]
    [InlineData("ORD123", "ORD123", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ord123", "ORD123", null)]
    public async Task Tampered_notification_is_rejected_without_provider_call(string signedId, string queryId, string? hash)
    {
        await using var factory = new BillingFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Notification(signedId, queryId, hash));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Unsigned_notification_is_rejected()
    {
        await using var factory = new BillingFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/webhooks/mercado-pago?data.id=ORD123&type=order", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Expired_signature_is_rejected()
    {
        await using var factory = new BillingFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Notification(timestamp: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task Signature_with_seconds_timestamp_is_accepted()
    {
        await using var factory = new BillingFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Notification(timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Provider_failure_returns_error_so_notification_can_be_retried()
    {
        await using var factory = new BillingFactory();
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse("{}", HttpStatusCode.InternalServerError));
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Notification());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
