using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Billing.Api.Saga;

public sealed class BillingSagaStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;

    public BillingSagaStore(IOptions<BillingSagaOptions> options, IHostEnvironment environment)
    {
        var path = Path.GetFullPath(options.Value.DatabasePath, environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS billing_orders (
                id TEXT PRIMARY KEY, payment_id TEXT UNIQUE, payment_key TEXT UNIQUE, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS billing_inbox (event_id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS billing_outbox (
                event_id TEXT PRIMARY KEY, os_id TEXT NOT NULL, routing_key TEXT NOT NULL,
                payload TEXT NOT NULL, sent INTEGER NOT NULL DEFAULT 0);
            """;
        command.ExecuteNonQuery();
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public BillingOrder? Get(Guid id) => Find("id", id.ToString("D"));
    public BillingOrder? FindPayment(string id) => Find("payment_id", id);
    public BillingOrder? FindKey(string key) => Find("payment_key", key);

    private BillingOrder? Find(string column, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // column provém exclusivamente das constantes acima.
        command.CommandText = $"SELECT data FROM billing_orders WHERE {column} = $value";
        command.Parameters.AddWithValue("$value", value);
        return command.ExecuteScalar() is string data ? JsonSerializer.Deserialize<BillingOrder>(data, Json) : null;
    }

    public bool WasConsumed(Guid eventId, string fingerprint)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint FROM billing_inbox WHERE event_id = $id";
        command.Parameters.AddWithValue("$id", eventId.ToString("D"));
        if (command.ExecuteScalar() is not string existing) return false;
        if (existing != fingerprint) throw new BillingRuleException(409, "eventId reutilizado com outro conteúdo.");
        return true;
    }

    public void Save(BillingOrder order, SagaOutgoingEvent? outgoing = null,
        Guid? consumedEvent = null, string? fingerprint = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO billing_orders(id, payment_id, payment_key, data) VALUES ($id, $payment, $key, $data)
            ON CONFLICT(id) DO UPDATE SET payment_id=$payment, payment_key=$key, data=$data;
            """;
        command.Parameters.AddWithValue("$id", order.OrdemServicoUniqueCode.ToString("D"));
        command.Parameters.AddWithValue("$payment", (object?)order.PaymentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)order.PaymentKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(order, Json));
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        if (order.CompensationRequested)
        {
            command.CommandText = "DELETE FROM billing_outbox WHERE os_id=$os AND routing_key='billing.aprovado' AND sent=0";
            command.Parameters.AddWithValue("$os", order.OrdemServicoUniqueCode.ToString("D"));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
        }
        if (outgoing is not null)
        {
            command.CommandText = """
                INSERT INTO billing_outbox(event_id, os_id, routing_key, payload) VALUES ($id,$os,$key,$data)
                ON CONFLICT(event_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", outgoing.Event.EventId.ToString("D"));
            command.Parameters.AddWithValue("$os", order.OrdemServicoUniqueCode.ToString("D"));
            command.Parameters.AddWithValue("$key", outgoing.RoutingKey);
            command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(outgoing.Event, Json));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
        }
        if (consumedEvent is not null)
        {
            command.CommandText = "INSERT INTO billing_inbox(event_id, fingerprint) VALUES ($id,$fingerprint)";
            command.Parameters.AddWithValue("$id", consumedEvent.Value.ToString("D"));
            command.Parameters.AddWithValue("$fingerprint", fingerprint!);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<BillingOrder> PendingOrders()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM billing_orders";
        using var reader = command.ExecuteReader();
        var result = new List<BillingOrder>();
        while (reader.Read())
        {
            var order = JsonSerializer.Deserialize<BillingOrder>(reader.GetString(0), Json)!;
            if (order.CompensationStatus != "manual_review" && (order.PaymentCreationPending ||
                (order.CompensationRequested ? order.CompensationStatus == "pending" : order.Decision == "payment_pending")))
                result.Add(order);
        }
        return result;
    }

    public IReadOnlyList<SagaOutgoingEvent> PendingEvents()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT routing_key, payload FROM billing_outbox WHERE sent=0 ORDER BY rowid LIMIT 100";
        using var reader = command.ExecuteReader();
        var result = new List<SagaOutgoingEvent>();
        while (reader.Read()) result.Add(new(reader.GetString(0), JsonSerializer.Deserialize<SagaEvent>(reader.GetString(1), Json)!));
        return result;
    }

    public void MarkSent(Guid eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE billing_outbox SET sent=1 WHERE event_id=$id";
        command.Parameters.AddWithValue("$id", eventId.ToString("D"));
        command.ExecuteNonQuery();
    }
}
