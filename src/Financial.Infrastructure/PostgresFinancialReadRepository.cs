using System.Data;
using Financial.Application;
using Npgsql;

namespace Financial.Infrastructure;

public sealed class PostgresFinancialReadRepository(NpgsqlDataSource dataSource) : IFinancialReadRepository
{
    public async Task<AccountResponse?> GetAccountAsync(Guid accountId, string ownerReference, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT created_at FROM financial_read_accounts WHERE account_id = @id AND owner_reference = @owner", connection);
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("owner", ownerReference);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        AccountResponse? result = await reader.ReadAsync(cancellationToken)
            ? new AccountResponse(accountId, reader.GetFieldValue<DateTimeOffset>(0))
            : null;
        await reader.DisposeAsync();
        await RecordAccessAsync(ownerReference, "account.read", accountId, connection, null, cancellationToken);
        return result;
    }

    public async Task<TransactionResponse?> GetTransactionAsync(Guid accountId, Guid transactionId, string ownerReference,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT entry.entry_id, entry.account_id, entry.entry_type, entry.amount_cents, entry.description, entry.occurred_at " +
            "FROM financial_read_entries entry JOIN financial_read_accounts account ON account.account_id = entry.account_id " +
            "WHERE entry.account_id = @account AND entry.entry_id = @entry AND account.owner_reference = @owner", connection);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("entry", transactionId);
        command.Parameters.AddWithValue("owner", ownerReference);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        TransactionResponse? result = null;
        if (await reader.ReadAsync(cancellationToken))
            result = new TransactionResponse(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));
        await reader.DisposeAsync();
        await RecordAccessAsync(ownerReference, "transaction.read", transactionId, connection, null, cancellationToken);
        return result;
    }

    public async Task<BalanceResponse?> GetBalanceAsync(Guid accountId, string ownerReference, DateTimeOffset? at,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using var accountCommand = new NpgsqlCommand(
            "SELECT balance_cents, created_at FROM financial_read_accounts " +
            "WHERE account_id = @id AND owner_reference = @owner", connection, transaction);
        accountCommand.Parameters.AddWithValue("id", accountId);
        accountCommand.Parameters.AddWithValue("owner", ownerReference);
        await using var accountReader = await accountCommand.ExecuteReaderAsync(cancellationToken);
        if (!await accountReader.ReadAsync(cancellationToken))
        {
            await accountReader.DisposeAsync();
            await RecordAccessAsync(ownerReference, "balance.read", accountId, connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var currentBalance = accountReader.GetInt64(0);
        var createdAt = accountReader.GetFieldValue<DateTimeOffset>(1);
        await accountReader.DisposeAsync();

        var balance = currentBalance;
        if (at is not null)
        {
            await using var historical = new NpgsqlCommand(
                "SELECT COALESCE(SUM(signed_amount_cents), 0) FROM financial_read_entries " +
                "WHERE account_id = @id AND occurred_at <= @at", connection, transaction);
            historical.Parameters.AddWithValue("id", accountId);
            historical.Parameters.AddWithValue("at", at.Value.ToUniversalTime());
            balance = ToBalanceInCents(await historical.ExecuteScalarAsync(cancellationToken));
        }

        var dataAsOf = await GetProjectionDataAsOfAsync(connection, transaction, createdAt, cancellationToken);
        await RecordAccessAsync(ownerReference, "balance.read", accountId, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BalanceResponse(accountId, balance, at ?? DateTimeOffset.UtcNow, dataAsOf);
    }

    public async Task<CustomerBalanceResponse?> GetCustomerBalanceAsync(string ownerReference, DateTimeOffset? at,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using var accounts = new NpgsqlCommand(
            "SELECT count(*), COALESCE(sum(balance_cents), 0), max(created_at) " +
            "FROM financial_read_accounts WHERE owner_reference = @owner", connection, transaction);
        accounts.Parameters.AddWithValue("owner", ownerReference);
        await using var reader = await accounts.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var count = checked((int)reader.GetInt64(0));
        if (count == 0)
        {
            await reader.DisposeAsync();
            await RecordAccessAsync(ownerReference, "customer.balance.read", null, connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var currentBalance = ToBalanceInCents(reader.GetValue(1));
        var latestAccountCreatedAt = reader.GetFieldValue<DateTimeOffset>(2);
        await reader.DisposeAsync();

        var balance = currentBalance;
        if (at is not null)
        {
            await using var historical = new NpgsqlCommand(
                "SELECT COALESCE(sum(entry.signed_amount_cents), 0) " +
                "FROM financial_read_entries entry JOIN financial_read_accounts account ON account.account_id = entry.account_id " +
                "WHERE account.owner_reference = @owner AND entry.occurred_at <= @at", connection, transaction);
            historical.Parameters.AddWithValue("owner", ownerReference);
            historical.Parameters.AddWithValue("at", at.Value.ToUniversalTime());
            balance = ToBalanceInCents(await historical.ExecuteScalarAsync(cancellationToken));
        }

        var dataAsOf = await GetProjectionDataAsOfAsync(connection, transaction, latestAccountCreatedAt, cancellationToken);
        await RecordAccessAsync(ownerReference, "customer.balance.read", null, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CustomerBalanceResponse(balance, at ?? DateTimeOffset.UtcNow, count, dataAsOf);
    }

    private static async Task<DateTimeOffset> GetProjectionDataAsOfAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, DateTimeOffset fallback, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT last_event_created_at FROM financial_projection_checkpoint WHERE checkpoint_id = 1", connection, transaction);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DateTimeOffset instant ? instant : fallback;
    }

    private static async Task RecordAccessAsync(string actorReference, string action, Guid? resourceId,
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO financial_read_access_audit (audit_id, actor_reference, action, resource_id, occurred_at) " +
            "VALUES (@id, @actor, @action, @resource, @occurred)", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("actor", actorReference);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("resource", (object?)resourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("occurred", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static long ToBalanceInCents(object? value)
    {
        try { return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch (OverflowException)
        {
            throw new FinancialApplicationException("O saldo consolidado excede o limite suportado.", 422);
        }
    }
}
