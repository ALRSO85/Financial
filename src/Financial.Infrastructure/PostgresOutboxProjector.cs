using System.Text.Json;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Npgsql;

namespace Financial.Infrastructure;

public sealed class PostgresOutboxProjector(NpgsqlDataSource writeDataSource, NpgsqlDataSource readDataSource)
{
    private const long ProjectorLockId = 8_416_072_311;
    private const int BatchSize = 500;
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
        var eventsJson = JsonSerializer.Serialize(pendingEvents.Select(pendingEvent =>
        {
            using var payload = JsonDocument.Parse(pendingEvent.Payload);
            return new
            {
                event_id = pendingEvent.Id,
                event_type = pendingEvent.Type,
                payload = payload.RootElement.Clone(),
                created_at = pendingEvent.CreatedAt.ToUniversalTime()
            };
        }));
        await using (var batch = new NpgsqlBatch(readConnection, readTransaction))
        {
            var appliedAt = DateTimeOffset.UtcNow;
            batch.BatchCommands.Add(CreateBatchCommand("""
                INSERT INTO financial_projected_events (event_id, projected_at)
                SELECT event_id, @appliedAt
                FROM jsonb_to_recordset(@events::jsonb) AS e(event_id uuid, event_type text, payload jsonb, created_at timestamptz)
                ON CONFLICT DO NOTHING
                """, eventsJson, appliedAt));
            batch.BatchCommands.Add(CreateBatchCommand("""
                INSERT INTO financial_read_accounts
                    (account_id, owner_reference, created_at, balance_cents, last_occurred_at, last_applied_at)
                SELECT (payload ->> 'accountId')::uuid,
                       payload ->> 'ownerReference',
                       (payload ->> 'createdAt')::timestamptz,
                       (payload ->> 'balanceInCents')::bigint,
                       NULL,
                       created_at
                FROM jsonb_to_recordset(@events::jsonb) AS e(event_id uuid, event_type text, payload jsonb, created_at timestamptz)
                WHERE event_type = 'account.opened'
                ON CONFLICT (account_id) DO NOTHING
                """, eventsJson));
            batch.BatchCommands.Add(CreateBatchCommand("""
                WITH events AS (
                    SELECT DISTINCT ON ((payload ->> 'accountId')::uuid)
                           payload, created_at,
                           (payload ->> 'occurredAt')::timestamptz AS occurred_at
                    FROM jsonb_to_recordset(@events::jsonb) AS e(event_id uuid, event_type text, payload jsonb, created_at timestamptz)
                    WHERE event_type = 'transaction.posted'
                    ORDER BY (payload ->> 'accountId')::uuid,
                             (payload ->> 'occurredAt')::timestamptz DESC
                )
                INSERT INTO financial_read_accounts
                    (account_id, owner_reference, created_at, balance_cents, last_occurred_at, last_applied_at)
                SELECT (payload ->> 'accountId')::uuid,
                       payload ->> 'ownerReference',
                       (payload ->> 'createdAt')::timestamptz,
                       (payload ->> 'balanceInCents')::bigint,
                       occurred_at,
                       created_at
                FROM events
                ON CONFLICT (account_id) DO UPDATE SET
                    balance_cents = EXCLUDED.balance_cents,
                    last_occurred_at = EXCLUDED.last_occurred_at,
                    last_applied_at = EXCLUDED.last_applied_at
                WHERE financial_read_accounts.last_occurred_at IS NULL
                   OR EXCLUDED.last_occurred_at > financial_read_accounts.last_occurred_at
                """, eventsJson));
            batch.BatchCommands.Add(CreateBatchCommand("""
                INSERT INTO financial_read_entries
                    (entry_id, account_id, entry_type, amount_cents, signed_amount_cents, idempotency_key, description, occurred_at)
                SELECT (payload ->> 'transactionId')::uuid,
                       (payload ->> 'accountId')::uuid,
                       payload ->> 'type',
                       (payload ->> 'amountInCents')::bigint,
                       (payload ->> 'signedAmountInCents')::bigint,
                       payload ->> 'idempotencyKey',
                       payload ->> 'description',
                       (payload ->> 'occurredAt')::timestamptz
                FROM jsonb_to_recordset(@events::jsonb) AS e(event_id uuid, event_type text, payload jsonb, created_at timestamptz)
                WHERE event_type = 'transaction.posted'
                ON CONFLICT DO NOTHING
                """, eventsJson));
            batch.BatchCommands.Add(CreateBatchCommand("""
                WITH events AS (
                    SELECT event_id, created_at
                    FROM jsonb_to_recordset(@events::jsonb) AS e(event_id uuid, event_type text, payload jsonb, created_at timestamptz)
                    ORDER BY created_at DESC, event_id DESC
                    LIMIT 1
                )
                INSERT INTO financial_projection_checkpoint
                    (checkpoint_id, last_event_created_at, last_event_id, updated_at)
                SELECT 1, created_at, event_id, @appliedAt FROM events
                ON CONFLICT (checkpoint_id) DO UPDATE SET
                    last_event_created_at = EXCLUDED.last_event_created_at,
                    last_event_id = EXCLUDED.last_event_id,
                    updated_at = EXCLUDED.updated_at
                WHERE (financial_projection_checkpoint.last_event_created_at, financial_projection_checkpoint.last_event_id)
                    < (EXCLUDED.last_event_created_at, EXCLUDED.last_event_id)
                """, eventsJson, appliedAt));
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
        await readTransaction.CommitAsync(cancellationToken);

        await using (var markProcessed = new NpgsqlCommand(
            "UPDATE financial_outbox SET processed_at = @now, attempts = attempts + 1, last_error = NULL " +
            "WHERE event_id = ANY(@ids) AND processed_at IS NULL", writeConnection, writeTransaction))
        {
            markProcessed.Parameters.AddWithValue("ids", pendingEvents.Select(item => item.Id).ToArray());
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

    private static NpgsqlBatchCommand CreateBatchCommand(string commandText, string eventsJson, DateTimeOffset? appliedAt = null)
    {
        var command = new NpgsqlBatchCommand(commandText);
        command.Parameters.AddWithValue("events", eventsJson);
        if (appliedAt is not null)
            command.Parameters.AddWithValue("appliedAt", appliedAt.Value);
        return command;
    }

    private sealed record PendingEvent(Guid Id, string Type, string Payload, DateTimeOffset CreatedAt);
}

public sealed record DeadLetterUpdate(Guid EventId, int Attempts, DateTimeOffset? DeadLetteredAt);
