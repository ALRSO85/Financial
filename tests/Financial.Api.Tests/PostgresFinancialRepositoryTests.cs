using Financial.Application;
using Financial.Infrastructure;
using Npgsql;
using Xunit;

namespace Financial.Api.Tests;

public sealed class PostgresFinancialRepositoryTests : IAsyncLifetime
{
    private readonly string? _adminConnectionString = Environment.GetEnvironmentVariable("FinancialTests__PostgresConnectionString");
    private NpgsqlDataSource _dataSource = null!;
    private NpgsqlDataSource _readDataSource = null!;
    private PostgresFinancialRepository _repository = null!;
    private PostgresFinancialReadRepository _readRepository = null!;
    private FinancialApplicationService _service = null!;
    private string _schema = null!;
    private string _readSchema = null!;
    private bool _writeDataSourceDisposed;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_adminConnectionString)) return;

        _schema = $"financial_test_{Guid.NewGuid():N}";
        _readSchema = $"financial_read_test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{_schema}\"", admin);
            await createSchema.ExecuteNonQueryAsync();
            await using var createReadSchema = new NpgsqlCommand($"CREATE SCHEMA \"{_readSchema}\"", admin);
            await createReadSchema.ExecuteNonQueryAsync();
        }

        var connection = new NpgsqlConnectionStringBuilder(_adminConnectionString) { SearchPath = _schema }.ConnectionString;
        _dataSource = NpgsqlDataSource.Create(connection);
        var readConnection = new NpgsqlConnectionStringBuilder(_adminConnectionString) { SearchPath = _readSchema }.ConnectionString;
        _readDataSource = NpgsqlDataSource.Create(readConnection);
        await new PostgresFinancialDatabaseInitializer(_dataSource).InitializeAsync(CancellationToken.None);
        await new PostgresFinancialReadDatabaseInitializer(_readDataSource).InitializeAsync(CancellationToken.None);
        _repository = new PostgresFinancialRepository(_dataSource);
        _readRepository = new PostgresFinancialReadRepository(_readDataSource);
        _service = new FinancialApplicationService(_repository, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_adminConnectionString) && !string.IsNullOrWhiteSpace(_schema))
        {
            if (!_writeDataSourceDisposed) await _dataSource.DisposeAsync();
            await _readDataSource.DisposeAsync();
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", admin);
            await dropSchema.ExecuteNonQueryAsync();
            await using var dropReadSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_readSchema}\" CASCADE", admin);
            await dropReadSchema.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Concurrent_debits_are_serialized_across_database_transactions()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        await _service.PostAsync(account.Id, "customer-123", new("credit", 10_000, "initial-credit", null), default);

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(async index =>
        {
            try
            {
                await _service.PostAsync(account.Id, "customer-123", new("debit", 200, $"debit-{index}", null), default);
                return true;
            }
            catch (FinancialApplicationException exception) when (exception.StatusCode == 422)
            {
                return false;
            }
        }));

        var snapshot = await _repository.LoadAsync(default);
        Assert.Equal(50, results.Count(succeeded => succeeded));
        Assert.Equal(51, snapshot.Entries.Count);
        Assert.Equal(0, snapshot.Accounts.Single().Balance.Cents);
    }

    [PostgresFact]
    public async Task Concurrent_retries_create_one_ledger_entry_and_one_outbox_event()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        var request = new PostTransactionRequest("credit", 750, "retry-key", "test");

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            _service.PostAsync(account.Id, "customer-123", request, default)));

        var snapshot = await _repository.LoadAsync(default);
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM financial_outbox WHERE event_type = 'transaction.posted'", connection);
        var outboxCount = (long)(await count.ExecuteScalarAsync() ?? 0L);
        await using var auditCountCommand = new NpgsqlCommand("SELECT count(*) FROM financial_audit_events", connection);
        var auditCount = (long)(await auditCountCommand.ExecuteScalarAsync() ?? 0L);
        Assert.Single(snapshot.Entries);
        Assert.Single(results, result => result.IsNew);
        Assert.Equal(19, results.Count(result => !result.IsNew));
        Assert.Equal(1, outboxCount);
        Assert.Equal(21, auditCount);
    }

    [PostgresFact]
    public async Task Projection_can_be_replayed_without_double_counting_and_returns_freshness()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        var posted = await _service.PostAsync(account.Id, "customer-123", new("credit", 750, "projection-credit", "depósito"), default);
        var projector = new PostgresOutboxProjector(_dataSource, _readDataSource);

        Assert.Equal(2, await projector.ProjectBatchAsync(default));
        Assert.Equal(0, await projector.ProjectBatchAsync(default));
        var balance = await _readRepository.GetBalanceAsync(account.Id, "customer-123", null, default);
        var storedTransaction = await _readRepository.GetTransactionAsync(account.Id, posted.Transaction.Id, "customer-123", default);

        Assert.NotNull(balance);
        Assert.Equal(750, balance.BalanceInCents);
        Assert.NotNull(balance.DataAsOf);
        Assert.NotNull(storedTransaction);
        Assert.Equal("depósito", storedTransaction.Description);
        Assert.Equal(750, storedTransaction.AmountInCents);
    }

    [PostgresFact]
    public async Task Read_repository_keeps_serving_the_projection_without_a_write_connection()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        var transaction = await _service.PostAsync(account.Id, "customer-123", new("credit", 900, "independent-read", null), default);
        var projector = new PostgresOutboxProjector(_dataSource, _readDataSource);
        await projector.ProjectBatchAsync(default);

        await _dataSource.DisposeAsync();
        _writeDataSourceDisposed = true;
        var balance = await _readRepository.GetBalanceAsync(account.Id, "customer-123", null, default);
        var storedTransaction = await _readRepository.GetTransactionAsync(account.Id, transaction.Transaction.Id, "customer-123", default);

        Assert.Equal(900, balance!.BalanceInCents);
        Assert.Equal(transaction.Transaction.Id, storedTransaction!.Id);
    }
}
