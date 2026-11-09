using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Financial.Application;
using Financial.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Financial.Infrastructure;

public sealed class PostgresFinancialRepository(NpgsqlDataSource dataSource) : IFinancialRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FinancialSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var accounts = new List<Account>();
        var entries = new List<FinancialEntry>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(
            "SELECT id, owner_reference, created_at, balance_cents FROM financial_accounts ORDER BY id", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts.Add(new Account(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                    new Money(reader.GetInt64(3))));
            }
        }

        await using (var command = new NpgsqlCommand(
            "SELECT id, account_id, entry_type, amount_cents, signed_amount_cents, idempotency_key, description, occurred_at " +
            "FROM financial_entries ORDER BY account_id, occurred_at", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(ReadEntry(reader));
        }

        return new(accounts, entries);
    }

    public async Task<TResult> ExecuteWriteAsync<TResult>(
        Func<IFinancialWriteSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var session = new PostgresWriteSession(connection, transaction);
        try
        {
            var result = await operation(session, cancellationToken);
            await session.FlushAsync(cancellationToken);
            await MeasureAsync("financial.database.commit", FinancialDatabaseTelemetry.CommitDuration,
                () => transaction.CommitAsync(cancellationToken));
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static FinancialEntry ReadEntry(NpgsqlDataReader reader)
    {
        var type = reader.GetString(2) switch
        {
            "credit" => TransactionType.Credit,
            "debit" => TransactionType.Debit,
            _ => throw new InvalidDataException("O banco contém um tipo de lançamento desconhecido.")
        };
        return new FinancialEntry(reader.GetGuid(0), reader.GetGuid(1), type,
            new Money(reader.GetInt64(3)), new Money(reader.GetInt64(4)), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7));
    }

    private sealed class PostgresWriteSession(NpgsqlConnection connection, NpgsqlTransaction transaction) : IFinancialWriteSession
    {
        private readonly Dictionary<Guid, Account> _lockedAccounts = [];
        private readonly List<NpgsqlBatchCommand> _pendingCommands = [];

        public Task AddAccountAsync(Account account, CancellationToken cancellationToken)
        {
            _lockedAccounts[account.Id] = account;
            var insert = new NpgsqlBatchCommand(
                "INSERT INTO financial_accounts (id, owner_reference, created_at, balance_cents, last_occurred_at) " +
                "VALUES (@id, @owner, @created, @balance, @created)");
            insert.Parameters.AddWithValue("id", account.Id);
            insert.Parameters.AddWithValue("owner", account.OwnerReference);
            insert.Parameters.AddWithValue("created", account.CreatedAt.ToUniversalTime());
            insert.Parameters.AddWithValue("balance", account.Balance.Cents);
            _pendingCommands.Add(insert);

            var payload = JsonSerializer.Serialize(new AccountProjectionEvent(account.Id, account.OwnerReference,
                account.CreatedAt, account.Balance.Cents), JsonOptions);
            AddOutboxEvent(Guid.NewGuid(), "account.opened", payload, account.CreatedAt);
            AddAuditEvent(account.OwnerReference, "account.opened", account.Id, null);
            return Task.CompletedTask;
        }

        public async Task<LockedAccountContext?> LockAccountAsync(Guid accountId, string ownerReference,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            using var activity = FinancialDatabaseTelemetry.ActivitySource.StartActivity("financial.database.account_lock");
            activity?.SetTag("db.system", "postgresql");
            activity?.SetTag("db.operation", "SELECT FOR UPDATE");
            var stopwatch = Stopwatch.StartNew();
            var outcome = "success";
            try
            {
                await using var batch = new NpgsqlBatch(connection, transaction);
                var lockAccount = new NpgsqlBatchCommand(
                    "SELECT id, owner_reference, created_at, balance_cents, last_occurred_at FROM financial_accounts " +
                    "WHERE id = @id AND owner_reference = @owner FOR UPDATE");
                lockAccount.Parameters.AddWithValue("id", accountId);
                lockAccount.Parameters.AddWithValue("owner", ownerReference);
                batch.BatchCommands.Add(lockAccount);

                var findIdempotent = new NpgsqlBatchCommand(
                    "SELECT id, account_id, entry_type, amount_cents, signed_amount_cents, idempotency_key, description, occurred_at " +
                    "FROM financial_entries WHERE account_id = @account AND idempotency_key = @key");
                findIdempotent.Parameters.AddWithValue("account", accountId);
                findIdempotent.Parameters.AddWithValue("key", idempotencyKey);
                batch.BatchCommands.Add(findIdempotent);

                await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
                Account? account = null;
                DateTimeOffset? lastRecordedAt = null;
                if (await reader.ReadAsync(cancellationToken))
                {
                    account = new Account(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                        new Money(reader.GetInt64(3)));
                    lastRecordedAt = reader.GetFieldValue<DateTimeOffset>(4);
                }

                await reader.NextResultAsync(cancellationToken);
                var existingEntry = await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
                if (account is null)
                {
                    outcome = "not_found";
                    activity?.SetTag("financial.outcome", outcome);
                    return null;
                }

                _lockedAccounts[account.Id] = account;
                activity?.SetTag("financial.outcome", outcome);
                return new LockedAccountContext(account, existingEntry, lastRecordedAt!.Value);
            }
            catch (Exception exception)
            {
                outcome = "error";
                activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                throw;
            }
            finally
            {
                FinancialDatabaseTelemetry.AccountLockDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("outcome", outcome));
            }
        }

        public Task UpdateAccountAsync(Account account, DateTimeOffset lastRecordedAt, CancellationToken cancellationToken)
        {
            if (!_lockedAccounts.ContainsKey(account.Id))
                throw new InvalidOperationException("A conta deve ser bloqueada antes de ser atualizada.");

            var command = new NpgsqlBatchCommand(
                "UPDATE financial_accounts SET balance_cents = @balance, last_occurred_at = @lastOccurredAt WHERE id = @id");
            command.Parameters.AddWithValue("id", account.Id);
            command.Parameters.AddWithValue("balance", account.Balance.Cents);
            command.Parameters.AddWithValue("lastOccurredAt", lastRecordedAt.ToUniversalTime());
            _pendingCommands.Add(command);
            _lockedAccounts[account.Id] = account;
            return Task.CompletedTask;
        }

        public Task AddEntryAsync(FinancialEntry entry, CancellationToken cancellationToken)
        {
            if (!_lockedAccounts.TryGetValue(entry.AccountId, out var account))
                throw new InvalidOperationException("A conta deve ser bloqueada antes de inserir um lançamento.");

            var command = new NpgsqlBatchCommand(
                "INSERT INTO financial_entries " +
                "(id, account_id, entry_type, amount_cents, signed_amount_cents, idempotency_key, description, occurred_at) " +
                "VALUES (@id, @account, @type, @amount, @signed, @key, @description, @occurred)");
            command.Parameters.AddWithValue("id", entry.Id);
            command.Parameters.AddWithValue("account", entry.AccountId);
            command.Parameters.AddWithValue("type", entry.Type == TransactionType.Credit ? "credit" : "debit");
            command.Parameters.AddWithValue("amount", entry.Amount.Cents);
            command.Parameters.AddWithValue("signed", entry.SignedAmount.Cents);
            command.Parameters.AddWithValue("key", entry.IdempotencyKey);
            command.Parameters.AddWithValue("description", (object?)entry.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("occurred", entry.OccurredAt.ToUniversalTime());
            _pendingCommands.Add(command);

            var payload = JsonSerializer.Serialize(new TransactionProjectionEvent(account.Id, account.OwnerReference,
                account.CreatedAt, account.Balance.Cents, entry.Id, entry.Type == TransactionType.Credit ? "credit" : "debit",
                entry.Amount.Cents, entry.SignedAmount.Cents, entry.IdempotencyKey, entry.Description, entry.OccurredAt), JsonOptions);
            AddOutboxEvent(entry.Id, "transaction.posted", payload, entry.OccurredAt);
            AddAuditEvent(account.OwnerReference, "transaction.posted", entry.AccountId, entry.Id);
            return Task.CompletedTask;
        }

        public async Task RecordAuditAsync(string action, Guid accountId, Guid? transactionId,
            CancellationToken cancellationToken)
        {
            if (!_lockedAccounts.TryGetValue(accountId, out var account))
            {
                await using var lookup = new NpgsqlCommand(
                    "SELECT owner_reference FROM financial_accounts WHERE id = @id", connection, transaction);
                lookup.Parameters.AddWithValue("id", accountId);
                var owner = await lookup.ExecuteScalarAsync(cancellationToken) as string;
                if (owner is null) throw new InvalidOperationException("Não foi possível auditar uma conta inexistente.");
                AddAuditEvent(owner, action, accountId, transactionId);
                return;
            }
            AddAuditEvent(account.OwnerReference, action, accountId, transactionId);
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_pendingCommands.Count == 0) return;
            using var activity = FinancialDatabaseTelemetry.ActivitySource.StartActivity("financial.database.write_batch");
            activity?.SetTag("db.system", "postgresql");
            activity?.SetTag("db.operation", "BATCH");
            activity?.SetTag("db.batch.command_count", _pendingCommands.Count);
            var stopwatch = Stopwatch.StartNew();
            var outcome = "success";
            await using var batch = new NpgsqlBatch(connection, transaction);
            foreach (var command in _pendingCommands)
                batch.BatchCommands.Add(command);
            try
            {
                await batch.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                outcome = "error";
                activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                throw;
            }
            finally
            {
                FinancialDatabaseTelemetry.WriteBatchDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("outcome", outcome));
                FinancialDatabaseTelemetry.WriteBatchCommandCount.Record(_pendingCommands.Count);
            }
        }

        private void AddAuditEvent(string actorReference, string action, Guid accountId, Guid? transactionId)
        {
            var audit = new NpgsqlBatchCommand(
                "INSERT INTO financial_audit_events (audit_id, actor_reference, action, account_id, transaction_id, occurred_at) " +
                "VALUES (@id, @actor, @action, @account, @transaction, @occurred)");
            audit.Parameters.AddWithValue("id", Guid.NewGuid());
            audit.Parameters.AddWithValue("actor", actorReference);
            audit.Parameters.AddWithValue("action", action);
            audit.Parameters.AddWithValue("account", accountId);
            audit.Parameters.AddWithValue("transaction", (object?)transactionId ?? DBNull.Value);
            audit.Parameters.AddWithValue("occurred", DateTimeOffset.UtcNow);
            _pendingCommands.Add(audit);
        }

        private void AddOutboxEvent(Guid eventId, string eventType, string payload, DateTimeOffset createdAt)
        {
            var command = new NpgsqlBatchCommand(
                "INSERT INTO financial_outbox (event_id, event_type, payload, created_at) " +
                "VALUES (@id, @type, CAST(@payload AS jsonb), @created)");
            command.Parameters.AddWithValue("id", eventId);
            command.Parameters.AddWithValue("type", eventType);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Text, payload);
            command.Parameters.AddWithValue("created", createdAt.ToUniversalTime());
            _pendingCommands.Add(command);
        }
    }

    private static async Task MeasureAsync(string operationName, Histogram<double> duration, Func<Task> operation)
    {
        using var activity = FinancialDatabaseTelemetry.ActivitySource.StartActivity(operationName);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.operation", "COMMIT");
        var stopwatch = Stopwatch.StartNew();
        var outcome = "success";
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            outcome = "error";
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }
        finally
        {
            duration.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private sealed record AccountProjectionEvent(Guid AccountId, string OwnerReference, DateTimeOffset CreatedAt,
        long BalanceInCents);

    private sealed record TransactionProjectionEvent(Guid AccountId, string OwnerReference, DateTimeOffset CreatedAt,
        long BalanceInCents, Guid TransactionId, string Type, long AmountInCents, long SignedAmountInCents,
        string IdempotencyKey, string? Description, DateTimeOffset OccurredAt);
}
