using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Billing.Api.Messaging;
using RabbitMQ.Client;

namespace Billing.Api.Tests;

public sealed class WebhookTests
{
    private static HttpRequestMessage Notification(
        string signedId = "ORD123", string queryId = "ORD123", string? hash = null, long? timestamp = null,
        string topic = "order")
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var digest = hash ?? Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(BillingFactory.Secret),
            Encoding.UTF8.GetBytes($"id:{signedId};request-id:test-request;ts:{ts};"))).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/mercado-pago?data.id={queryId}&type={topic}")
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
        var published = factory.Publisher.Events.ToArray();
        Assert.Equal(2, published.Length);
        Assert.Equal(published[0].EventId, published[1].EventId);
        Assert.Equal("ORD123", published[0].PaymentId);
        Assert.Equal("pedido-123", published[0].OrderId);
        Assert.Equal(50m, published[0].Amount);
        Assert.Equal("action_required", published[0].Status);
        Assert.Equal("waiting_transfer", published[0].StatusDetail);
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
        Assert.Empty(factory.Publisher.Events);
    }

    [Fact]
    public async Task Unsigned_notification_is_rejected()
    {
        await using var factory = new BillingFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/webhooks/mercado-pago?data.id=ORD123&type=order", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Requests);
        Assert.Empty(factory.Publisher.Events);
    }

    [Fact]
    public async Task Expired_signature_is_rejected()
    {
        await using var factory = new BillingFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Notification(timestamp: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Requests);
        Assert.Empty(factory.Publisher.Events);
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
        Assert.Empty(factory.Publisher.Events);
    }

    [Fact]
    public async Task Other_topics_are_acknowledged_without_querying_or_publishing()
    {
        await using var factory = new BillingFactory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Notification(topic: "payment"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, factory.Requests);
        Assert.Empty(factory.Publisher.Events);
    }

    [Theory]
    [InlineData("processed", "accredited")]
    [InlineData("refunded", "refunded")]
    public async Task Event_uses_current_provider_state_instead_of_notification_body(string status, string detail)
    {
        await using var factory = new BillingFactory();
        factory.Respond = (_, _) => Task.FromResult(BillingFactory.JsonResponse(
            JsonSerializer.Serialize(new
            {
                id = "ORD123", external_reference = "pedido-123", total_amount = "50.00",
                status, status_detail = detail
            })));
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Notification());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var published = Assert.Single(factory.Publisher.Events);
        Assert.Equal(status, published.Status);
        Assert.Equal(detail, published.StatusDetail);
    }

    [Fact]
    public async Task Broker_failure_returns_safe_503_and_redelivery_can_succeed()
    {
        await using var factory = new BillingFactory();
        factory.Publisher.OnPublish = (_, _) => throw new MessagingUnavailableException();
        using var client = factory.CreateClient();
        using var failed = await client.SendAsync(Notification());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("application/problem+json", failed.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain(factory.AccessToken, await failed.Content.ReadAsStringAsync());
        Assert.Empty(factory.Publisher.Events);

        factory.Publisher.OnPublish = (_, _) => Task.CompletedTask;
        using var retried = await client.SendAsync(Notification());
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Single(factory.Publisher.Events);
        Assert.Equal(2, factory.Requests);
    }

    [Fact]
    public async Task Webhook_waits_for_publication_before_responding()
    {
        await using var factory = new BillingFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Publisher.OnPublish = async (_, ct) =>
        {
            started.SetResult();
            await confirmed.Task.WaitAsync(ct);
        };
        using var client = factory.CreateClient();
        var pending = client.SendAsync(Notification());
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            confirmed.TrySetResult();
        }
        using var response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Publisher.Events);
    }

    [RabbitMqFact]
    [Trait("Category", "RabbitMq")]
    public async Task Signed_webhook_delivers_event_to_real_broker()
    {
        await using var factory = new BillingFactory { UseRabbitMq = true };
        var rabbit = new ConnectionFactory { UserName = "billing", Password = "billing-local" };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await rabbit.CreateConnectionAsync(deadline.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: deadline.Token);
        using var client = factory.CreateClient();
        try
        {
            using var response = await client.SendAsync(Notification(), deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var message = await channel.BasicGetAsync(factory.RabbitMqResource, autoAck: true, deadline.Token);
            Assert.NotNull(message);
            using var body = JsonDocument.Parse(message.Body);
            Assert.Equal("ORD123", body.RootElement.GetProperty("paymentId").GetString());
            Assert.Equal("waiting_transfer", body.RootElement.GetProperty("statusDetail").GetString());
            Assert.Equal(body.RootElement.GetProperty("eventId").GetString(), message.BasicProperties.MessageId);
            Assert.Equal(1, factory.Requests);
        }
        finally
        {
            await channel.QueueDeleteAsync(factory.RabbitMqResource, cancellationToken: deadline.Token);
            await channel.ExchangeDeleteAsync(factory.RabbitMqResource, cancellationToken: deadline.Token);
        }
    }
}
