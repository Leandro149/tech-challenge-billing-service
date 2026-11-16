using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Billing.Api.Integrations;

public static class WebhookSignatureValidator
{
    public static bool IsValid(string signature, string? requestId, string dataId, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(dataId)) return false;
        string? timestamp = null;
        string? receivedHash = null;
        foreach (var part in signature.Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) return false;
            if (pair[0] == "ts")
            {
                if (timestamp is not null) return false;
                timestamp = pair[1];
            }
            if (pair[0] == "v1")
            {
                if (receivedHash is not null) return false;
                receivedHash = pair[1];
            }
        }
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixTime) ||
            receivedHash?.Length != 64) return false;

        // A documentação apresenta timestamps em milissegundos; aceita também segundos.
        DateTimeOffset signedAt;
        try
        {
            signedAt = unixTime > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixTime)
                : DateTimeOffset.FromUnixTimeSeconds(unixTime);
        }
        catch (ArgumentOutOfRangeException) { return false; }
        if ((DateTimeOffset.UtcNow - signedAt).Duration() > TimeSpan.FromMinutes(5)) return false;

        // Preserve a capitalização do data.id recebido, conforme o SDK .NET oficial.
        var manifest = $"id:{dataId.Trim()};";
        if (!string.IsNullOrWhiteSpace(requestId)) manifest += $"request-id:{requestId.Trim()};";
        manifest += $"ts:{timestamp};";
        var expectedHash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedHash, Convert.FromHexString(receivedHash));
        }
        catch (FormatException) { return false; }
    }
}
