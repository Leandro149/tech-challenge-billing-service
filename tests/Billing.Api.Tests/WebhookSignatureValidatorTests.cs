using System.Security.Cryptography;
using System.Text;
using Billing.Api.Integrations;

namespace Billing.Api.Tests;

[Trait("Category", "Unit")]
public sealed class WebhookSignatureValidatorTests
{
    private const string Secret = "signature-unit-test-secret";

    private static string Sign(long timestamp, string dataId = "ORD123", string? requestId = "request-123")
    {
        var manifest = requestId is null
            ? $"id:{dataId};ts:{timestamp};"
            : $"id:{dataId};request-id:{requestId};ts:{timestamp};";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(manifest));
        return $"ts={timestamp},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_valid_signatures_with_seconds_or_milliseconds(bool milliseconds)
    {
        var timestamp = milliseconds ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(WebhookSignatureValidator.IsValid(Sign(timestamp), "request-123", "ORD123", Secret));
    }

    [Fact]
    public void Accepts_a_signature_without_optional_request_id()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(WebhookSignatureValidator.IsValid(Sign(timestamp, requestId: null), null, "ORD123", Secret));
    }

    [Theory]
    [InlineData("ORD999", "request-123", Secret)]
    [InlineData("ord123", "request-123", Secret)]
    [InlineData("ORD123", "different-request", Secret)]
    [InlineData("ORD123", "request-123", "different-secret")]
    public void Rejects_changes_to_signed_data_request_or_secret(string dataId, string requestId, string secret)
    {
        var signature = Sign(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.False(WebhookSignatureValidator.IsValid(signature, requestId, dataId, secret));
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    public void Rejects_expired_or_future_signatures(int offsetMinutes)
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(offsetMinutes).ToUnixTimeSeconds();
        Assert.False(WebhookSignatureValidator.IsValid(Sign(timestamp), "request-123", "ORD123", Secret));
    }

    [Theory]
    [InlineData(",ts=1")]
    [InlineData(",v1=duplicate")]
    [InlineData(",malformed-part")]
    public void Rejects_duplicate_or_malformed_signature_fields(string suffix)
    {
        var signature = Sign(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + suffix;
        Assert.False(WebhookSignatureValidator.IsValid(signature, "request-123", "ORD123", Secret));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void Rejects_invalid_timestamp_range(long timestamp)
    {
        Assert.False(WebhookSignatureValidator.IsValid(Sign(timestamp), "request-123", "ORD123", Secret));
    }

    [Fact]
    public void Rejects_a_hash_that_is_not_hexadecimal()
    {
        var signature = $"ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()},v1={new string('z', 64)}";
        Assert.False(WebhookSignatureValidator.IsValid(signature, "request-123", "ORD123", Secret));
    }
}
