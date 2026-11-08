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
            await transaction.CommitAsync(cancellationToken);
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

        public async Task AddAccountAsync(Account account, CancellationToken cancellationToken)
        {
            await using (var command = new NpgsqlCommand(
                "INSERT INTO financial_accounts (id, owner_reference, created_at, balance_cents) VALUES (@id, @owner, @created, @balance)",
                connection, transaction))
            {
                command.Parameters.AddWithValue("id", account.Id);
                command.Parameters.AddWithValue("owner", account.OwnerReference);
                command.Parameters.AddWithValue("created", account.CreatedAt.ToUniversalTime());
                command.Parameters.AddWithValue("balance", account.Balance.Cents);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var payload = JsonSerializer.Serialize(new AccountProjectionEvent(account.Id, account.OwnerReference,
                account.CreatedAt, account.Balance.Cents), JsonOptions);
            await AddOutboxEventAsync(Guid.NewGuid(), "account.opened", payload, account.CreatedAt, cancellationToken);
            await RecordAuditAsync("account.opened", account.Id, null, cancellationToken);
        }

        public async Task<Account?> GetAccountForUpdateAsync(Guid accountId, string ownerReference, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(
                "SELECT id, owner_reference, created_at, balance_cents FROM financial_accounts " +
                "WHERE id = @id AND owner_reference = @owner FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("id", accountId);
            command.Parameters.AddWithValue("owner", ownerReference);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;

            var account = new Account(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                new Money(reader.GetInt64(3)));
            _lockedAccounts[account.Id] = account;
            return account;
        }

        public async Task<FinancialEntry?> FindByIdempotencyKeyAsync(Guid accountId, string idempotencyKey,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(
                "SELECT id, account_id, entry_type, amount_cents, signed_amount_cents, idempotency_key, description, occurred_at " +
                "FROM financial_entries WHERE account_id = @account AND idempotency_key = @key",
                connection, transaction);
            command.Parameters.AddWithValue("account", accountId);
            command.Parameters.AddWithValue("key", idempotencyKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
        }

        public async Task<DateTimeOffset> GetLastRecordedAtAsync(Guid accountId, DateTimeOffset createdAt,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(
                "SELECT COALESCE(MAX(occurred_at), @created) FROM financial_entries WHERE account_id = @account",
                connection, transaction);
            command.Parameters.AddWithValue("account", accountId);
            command.Parameters.AddWithValue("created", createdAt.ToUniversalTime());
            var result = await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidDataException("Não foi possível obter o instante do último lançamento.");
            return result switch
            {
                DateTimeOffset instant => instant,
                DateTime instant => new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
                _ => throw new InvalidDataException("O banco retornou um tipo inválido para o instante do último lançamento.")
            };
        }

        public async Task UpdateAccountAsync(Account account, CancellationToken cancellationToken)
        {
            if (!_lockedAccounts.ContainsKey(account.Id))
                throw new InvalidOperationException("A conta deve ser bloqueada antes de ser atualizada.");

            await using var command = new NpgsqlCommand(
                "UPDATE financial_accounts SET balance_cents = @balance WHERE id = @id", connection, transaction);
            command.Parameters.AddWithValue("id", account.Id);
            command.Parameters.AddWithValue("balance", account.Balance.Cents);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("A conta deixou de existir durante a transação.");
            _lockedAccounts[account.Id] = account;
        }

        public async Task AddEntryAsync(FinancialEntry entry, CancellationToken cancellationToken)
        {
            if (!_lockedAccounts.TryGetValue(entry.AccountId, out var account))
                throw new InvalidOperationException("A conta deve ser bloqueada antes de inserir um lançamento.");

            await using (var command = new NpgsqlCommand(
                "INSERT INTO financial_entries " +
                "(id, account_id, entry_type, amount_cents, signed_amount_cents, idempotency_key, description, occurred_at) " +
                "VALUES (@id, @account, @type, @amount, @signed, @key, @description, @occurred)",
                connection, transaction))
            {
                command.Parameters.AddWithValue("id", entry.Id);
                command.Parameters.AddWithValue("account", entry.AccountId);
                command.Parameters.AddWithValue("type", entry.Type == TransactionType.Credit ? "credit" : "debit");
                command.Parameters.AddWithValue("amount", entry.Amount.Cents);
                command.Parameters.AddWithValue("signed", entry.SignedAmount.Cents);
                command.Parameters.AddWithValue("key", entry.IdempotencyKey);
                command.Parameters.AddWithValue("description", (object?)entry.Description ?? DBNull.Value);
                command.Parameters.AddWithValue("occurred", entry.OccurredAt.ToUniversalTime());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var payload = JsonSerializer.Serialize(new TransactionProjectionEvent(account.Id, account.OwnerReference,
                account.CreatedAt, account.Balance.Cents, entry.Id, entry.Type == TransactionType.Credit ? "credit" : "debit",
                entry.Amount.Cents, entry.SignedAmount.Cents, entry.IdempotencyKey, entry.Description, entry.OccurredAt), JsonOptions);
            await AddOutboxEventAsync(entry.Id, "transaction.posted", payload, entry.OccurredAt, cancellationToken);
            await RecordAuditAsync("transaction.posted", entry.AccountId, entry.Id, cancellationToken);
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
                await InsertAuditAsync(owner, action, accountId, transactionId, cancellationToken);
                return;
            }
            await InsertAuditAsync(account.OwnerReference, action, accountId, transactionId, cancellationToken);
        }

        private async Task InsertAuditAsync(string actorReference, string action, Guid accountId, Guid? transactionId,
            CancellationToken cancellationToken)
        {
            await using var audit = new NpgsqlCommand(
                "INSERT INTO financial_audit_events (audit_id, actor_reference, action, account_id, transaction_id, occurred_at) " +
                "VALUES (@id, @actor, @action, @account, @transaction, @occurred)", connection, transaction);
            audit.Parameters.AddWithValue("id", Guid.NewGuid());
            audit.Parameters.AddWithValue("actor", actorReference);
            audit.Parameters.AddWithValue("action", action);
            audit.Parameters.AddWithValue("account", accountId);
            audit.Parameters.AddWithValue("transaction", (object?)transactionId ?? DBNull.Value);
            audit.Parameters.AddWithValue("occurred", DateTimeOffset.UtcNow);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task AddOutboxEventAsync(Guid eventId, string eventType, string payload, DateTimeOffset createdAt,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO financial_outbox (event_id, event_type, payload, created_at) " +
                "VALUES (@id, @type, CAST(@payload AS jsonb), @created)", connection, transaction);
            command.Parameters.AddWithValue("id", eventId);
            command.Parameters.AddWithValue("type", eventType);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Text, payload);
            command.Parameters.AddWithValue("created", createdAt.ToUniversalTime());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed record AccountProjectionEvent(Guid AccountId, string OwnerReference, DateTimeOffset CreatedAt,
        long BalanceInCents);

    private sealed record TransactionProjectionEvent(Guid AccountId, string OwnerReference, DateTimeOffset CreatedAt,
        long BalanceInCents, Guid TransactionId, string Type, long AmountInCents, long SignedAmountInCents,
        string IdempotencyKey, string? Description, DateTimeOffset OccurredAt);
}
