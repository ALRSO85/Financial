using System.Text.Json;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Npgsql;

namespace Financial.Infrastructure;

public sealed class PostgresOutboxProjector(NpgsqlDataSource writeDataSource, NpgsqlDataSource readDataSource)
{
    private const long ProjectorLockId = 8_416_072_311;
    private const int BatchSize = 100;
    private static readonly Meter Meter = new("Financial.Infrastructure", "1.0.0");
    private static readonly ActivitySource ActivitySource = new("Financial.Infrastructure");
    private static readonly Counter<long> ProjectedEvents = Meter.CreateCounter<long>("financial.projection.events");
    private static readonly Histogram<double> BatchDuration = Meter.CreateHistogram<double>("financial.projection.batch.duration", "ms");
    private static long _pendingEventCount;
    private static long _deadLetterEventCount;
    private static long _oldestPendingUtcTicks;

    private static readonly ObservableGauge<long> PendingEventsGauge = Meter.CreateObservableGauge(
        "financial.outbox.pending_events", () => Interlocked.Read(ref _pendingEventCount));
    private static readonly ObservableGauge<long> DeadLetterEventsGauge = Meter.CreateObservableGauge(
        "financial.outbox.dead_letter_events", () => Interlocked.Read(ref _deadLetterEventCount));
    private static readonly ObservableGauge<double> OldestPendingAgeGauge = Meter.CreateObservableGauge(
        "financial.outbox.oldest_pending_age", () =>
        {
            var ticks = Interlocked.Read(ref _oldestPendingUtcTicks);
            return ticks == 0 ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero)).TotalSeconds);
        }, "s");

    public async Task<int> ProjectBatchAsync(CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("financial.projection.batch");
        var startedAt = Stopwatch.GetTimestamp();
        await using var writeConnection = await writeDataSource.OpenConnectionAsync(cancellationToken);
        await using var writeTransaction = await writeConnection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@id)", writeConnection, writeTransaction))
        {
            lockCommand.Parameters.AddWithValue("id", ProjectorLockId);
            if ((bool)(await lockCommand.ExecuteScalarAsync(cancellationToken) ?? false) is false)
                return 0;
        }

        var pendingEvents = new List<PendingEvent>();
        await using (var select = new NpgsqlCommand(
            "SELECT event_id, event_type, payload::text, created_at FROM financial_outbox " +
            "WHERE processed_at IS NULL AND dead_lettered_at IS NULL ORDER BY created_at, event_id LIMIT @limit FOR UPDATE",
            writeConnection, writeTransaction))
        {
            select.Parameters.AddWithValue("limit", BatchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                pendingEvents.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetFieldValue<DateTimeOffset>(3)));
        }

        if (pendingEvents.Count == 0)
        {
            await writeTransaction.CommitAsync(cancellationToken);
            BatchDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return 0;
        }

        await using var readConnection = await readDataSource.OpenConnectionAsync(cancellationToken);
        await using var readTransaction = await readConnection.BeginTransactionAsync(cancellationToken);
        foreach (var pendingEvent in pendingEvents)
            await ApplyEventAsync(pendingEvent, readConnection, readTransaction, cancellationToken);
        await readTransaction.CommitAsync(cancellationToken);

        foreach (var pendingEvent in pendingEvents)
        {
            await using var markProcessed = new NpgsqlCommand(
                "UPDATE financial_outbox SET processed_at = @now, attempts = attempts + 1, last_error = NULL " +
                "WHERE event_id = @id AND processed_at IS NULL", writeConnection, writeTransaction);
            markProcessed.Parameters.AddWithValue("id", pendingEvent.Id);
            markProcessed.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            await markProcessed.ExecuteNonQueryAsync(cancellationToken);
        }
        await writeTransaction.CommitAsync(cancellationToken);
        ProjectedEvents.Add(pendingEvents.Count);
        BatchDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        return pendingEvents.Count;
    }

    public async Task RefreshProjectionStatusAsync(CancellationToken cancellationToken)
    {
        long pendingCount;
        long deadLetterCount;
        DateTimeOffset? oldestPendingAt;
        await using (var connection = await writeDataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE processed_at IS NULL AND dead_lettered_at IS NULL),
                   min(created_at) FILTER (WHERE processed_at IS NULL AND dead_lettered_at IS NULL),
                   count(*) FILTER (WHERE dead_lettered_at IS NOT NULL)
            FROM financial_outbox
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            pendingCount = reader.GetInt64(0);
            oldestPendingAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            deadLetterCount = reader.GetInt64(2);
        }

        Interlocked.Exchange(ref _pendingEventCount, pendingCount);
        Interlocked.Exchange(ref _deadLetterEventCount, deadLetterCount);
        Interlocked.Exchange(ref _oldestPendingUtcTicks, oldestPendingAt?.UtcDateTime.Ticks ?? 0);

        await using var readConnection = await readDataSource.OpenConnectionAsync(cancellationToken);
        await using var update = new NpgsqlCommand("""
            INSERT INTO financial_projection_status
                (status_id, worker_heartbeat_at, pending_count, oldest_pending_at, dead_letter_count, updated_at)
            VALUES (1, @heartbeat, @pending, @oldest, @dead, @updated)
            ON CONFLICT (status_id) DO UPDATE SET
                worker_heartbeat_at = EXCLUDED.worker_heartbeat_at,
                pending_count = EXCLUDED.pending_count,
                oldest_pending_at = EXCLUDED.oldest_pending_at,
                dead_letter_count = EXCLUDED.dead_letter_count,
                updated_at = EXCLUDED.updated_at
            """, readConnection);
        var now = DateTimeOffset.UtcNow;
        update.Parameters.AddWithValue("heartbeat", now);
        update.Parameters.AddWithValue("pending", pendingCount);
        update.Parameters.AddWithValue("oldest", (object?)oldestPendingAt ?? DBNull.Value);
        update.Parameters.AddWithValue("dead", deadLetterCount);
        update.Parameters.AddWithValue("updated", now);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeadLetterUpdate?> RecordPermanentFailureAsync(string errorCode, CancellationToken cancellationToken)
    {
        await using var connection = await writeDataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH candidate AS (
                SELECT event_id FROM financial_outbox
                WHERE processed_at IS NULL AND dead_lettered_at IS NULL
                ORDER BY created_at, event_id LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE financial_outbox AS outbox
            SET attempts = outbox.attempts + 1,
                last_error = @error,
                dead_lettered_at = CASE WHEN outbox.attempts + 1 >= 10 THEN now() ELSE NULL END
            FROM candidate
            WHERE outbox.event_id = candidate.event_id
            RETURNING outbox.event_id, outbox.attempts, outbox.dead_lettered_at
            """, connection);
        command.Parameters.AddWithValue("error", errorCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new DeadLetterUpdate(reader.GetGuid(0), reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static async Task ApplyEventAsync(PendingEvent pendingEvent, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using (var record = new NpgsqlCommand(
            "INSERT INTO financial_projected_events (event_id, projected_at) VALUES (@id, @now) ON CONFLICT DO NOTHING",
            connection, transaction))
        {
            record.Parameters.AddWithValue("id", pendingEvent.Id);
            record.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            if (await record.ExecuteNonQueryAsync(cancellationToken) == 0) return;
        }

        using var document = JsonDocument.Parse(pendingEvent.Payload);
        var payload = document.RootElement;
        if (pendingEvent.Type == "account.opened")
        {
            await using var account = new NpgsqlCommand(
                "INSERT INTO financial_read_accounts " +
                "(account_id, owner_reference, created_at, balance_cents, last_occurred_at, last_applied_at) " +
                "VALUES (@id, @owner, @created, @balance, NULL, @applied) ON CONFLICT (account_id) DO NOTHING",
                connection, transaction);
            account.Parameters.AddWithValue("id", payload.GetProperty("accountId").GetGuid());
            account.Parameters.AddWithValue("owner", payload.GetProperty("ownerReference").GetString()!);
            account.Parameters.AddWithValue("created", payload.GetProperty("createdAt").GetDateTimeOffset().ToUniversalTime());
            account.Parameters.AddWithValue("balance", payload.GetProperty("balanceInCents").GetInt64());
            account.Parameters.AddWithValue("applied", pendingEvent.CreatedAt.ToUniversalTime());
            await account.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (pendingEvent.Type == "transaction.posted")
        {
            var accountId = payload.GetProperty("accountId").GetGuid();
            var occurredAt = payload.GetProperty("occurredAt").GetDateTimeOffset().ToUniversalTime();
            var createdAt = payload.GetProperty("createdAt").GetDateTimeOffset().ToUniversalTime();
            var owner = payload.GetProperty("ownerReference").GetString()!;
            var balance = payload.GetProperty("balanceInCents").GetInt64();

            await using (var account = new NpgsqlCommand(
                "INSERT INTO financial_read_accounts " +
                "(account_id, owner_reference, created_at, balance_cents, last_occurred_at, last_applied_at) " +
                "VALUES (@id, @owner, @created, @balance, @occurred, @applied) " +
                "ON CONFLICT (account_id) DO UPDATE SET balance_cents = EXCLUDED.balance_cents, " +
                "last_occurred_at = EXCLUDED.last_occurred_at, last_applied_at = EXCLUDED.last_applied_at " +
                "WHERE financial_read_accounts.last_occurred_at IS NULL OR " +
                "EXCLUDED.last_occurred_at > financial_read_accounts.last_occurred_at",
                connection, transaction))
            {
                account.Parameters.AddWithValue("id", accountId);
                account.Parameters.AddWithValue("owner", owner);
                account.Parameters.AddWithValue("created", createdAt);
                account.Parameters.AddWithValue("balance", balance);
                account.Parameters.AddWithValue("occurred", occurredAt);
                account.Parameters.AddWithValue("applied", pendingEvent.CreatedAt.ToUniversalTime());
                await account.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var entry = new NpgsqlCommand(
                "INSERT INTO financial_read_entries " +
                "(entry_id, account_id, entry_type, amount_cents, signed_amount_cents, idempotency_key, description, occurred_at) " +
                "VALUES (@id, @account, @type, @amount, @signed, @key, @description, @occurred) ON CONFLICT DO NOTHING",
                connection, transaction);
            entry.Parameters.AddWithValue("id", payload.GetProperty("transactionId").GetGuid());
            entry.Parameters.AddWithValue("account", accountId);
            entry.Parameters.AddWithValue("type", payload.GetProperty("type").GetString()!);
            entry.Parameters.AddWithValue("amount", payload.GetProperty("amountInCents").GetInt64());
            entry.Parameters.AddWithValue("signed", payload.GetProperty("signedAmountInCents").GetInt64());
            entry.Parameters.AddWithValue("key", payload.GetProperty("idempotencyKey").GetString()!);
            var description = payload.GetProperty("description");
            entry.Parameters.AddWithValue("description", description.ValueKind == JsonValueKind.Null
                ? DBNull.Value : description.GetString()!);
            entry.Parameters.AddWithValue("occurred", occurredAt);
            await entry.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            throw new InvalidDataException($"Tipo de evento desconhecido na outbox: {pendingEvent.Type}.");
        }

        await using var checkpoint = new NpgsqlCommand(
            "INSERT INTO financial_projection_checkpoint (checkpoint_id, last_event_created_at, last_event_id, updated_at) " +
            "VALUES (1, @created, @id, @now) ON CONFLICT (checkpoint_id) DO UPDATE SET " +
            "last_event_created_at = EXCLUDED.last_event_created_at, last_event_id = EXCLUDED.last_event_id, updated_at = EXCLUDED.updated_at " +
            "WHERE (financial_projection_checkpoint.last_event_created_at, financial_projection_checkpoint.last_event_id) " +
            "< (EXCLUDED.last_event_created_at, EXCLUDED.last_event_id)", connection, transaction);
        checkpoint.Parameters.AddWithValue("created", pendingEvent.CreatedAt.ToUniversalTime());
        checkpoint.Parameters.AddWithValue("id", pendingEvent.Id);
        checkpoint.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await checkpoint.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record PendingEvent(Guid Id, string Type, string Payload, DateTimeOffset CreatedAt);
}

public sealed record DeadLetterUpdate(Guid EventId, int Attempts, DateTimeOffset? DeadLetteredAt);
