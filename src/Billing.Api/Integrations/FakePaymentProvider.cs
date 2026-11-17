using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Billing.Api.Contracts;
using Billing.Api.Saga;
using Microsoft.Data.Sqlite;

namespace Billing.Api.Integrations;

internal sealed class FakePaymentProvider : IPaymentProvider
{
    internal const string IdPrefix = "ORD-FAKE-";
    private readonly BillingSagaStore store;

    public FakePaymentProvider(BillingSagaStore store)
    {
        this.store = store;
        using var connection = store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS fake_payments (
                id TEXT PRIMARY KEY, idempotency_key TEXT NOT NULL UNIQUE, payload_hash TEXT NOT NULL,
                order_id TEXT NOT NULL, amount TEXT NOT NULL, refunded INTEGER NOT NULL DEFAULT 0);
            """;
        command.ExecuteNonQuery();
    }

    public Task<PaymentResponse> CreatePixAsync(CreatePixRequest input, string idempotencyKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var amount = input.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { input.OrderId, Amount = amount, input.PayerEmail, input.PayerFirstName })));
        using var connection = store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO fake_payments (id, idempotency_key, payload_hash, order_id, amount)
            VALUES ($id, $key, $hash, $order, $amount) ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", $"{IdPrefix}{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$order", input.OrderId);
        command.Parameters.AddWithValue("$amount", amount);
        command.ExecuteNonQuery();

        command.CommandText = "SELECT id, order_id, amount, refunded, payload_hash FROM fake_payments WHERE idempotency_key=$key";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(4) != hash)
            throw new BillingRuleException(409, "Chave de idempotência já usada com outros dados no provedor Fake.");
        return Task.FromResult(Map(reader));
    }

    public Task<PaymentResponse> GetAsync(string orderId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, order_id, amount, refunded FROM fake_payments WHERE id=$id";
        command.Parameters.AddWithValue("$id", orderId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw PaymentNotFound();
        return Task.FromResult(Map(reader));
    }

    public async Task<PaymentFinancialState> GetFinancialStateAsync(string orderId, CancellationToken ct)
    {
        var payment = await GetAsync(orderId, ct);
        return new(payment, payment.Status == "refunded", RefundInProgress: false);
    }

    public Task RefundAsync(string orderId, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = store.Open();
        using var command = connection.CreateCommand();
        // Uma única transição persistente por pagamento. Repetir o estorno integral mantém o resultado.
        command.CommandText = "UPDATE fake_payments SET refunded=1 WHERE id=$id";
        command.Parameters.AddWithValue("$id", orderId);
        if (command.ExecuteNonQuery() == 0) throw PaymentNotFound();
        return Task.CompletedTask;
    }

    public async Task CancelAsync(string orderId, string key, CancellationToken ct)
    {
        await GetAsync(orderId, ct);
        throw new BillingRuleException(409, "Pagamentos Fake são aprovados imediatamente e admitem apenas estorno.");
    }

    private static PaymentResponse Map(SqliteDataReader reader)
    {
        var refunded = reader.GetBoolean(3);
        return new(reader.GetString(0), reader.GetString(1), decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            refunded ? "refunded" : "processed", refunded ? "refunded" : "accredited", null, null, null);
    }

    private static BillingRuleException PaymentNotFound() => new(404, "Cobrança não encontrada no provedor Fake.");
}
