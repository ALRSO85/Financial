using System.Text.Json;
using Ledger.Application;
using Ledger.Domain;
using Ledger.Infrastructure;
using Xunit;

namespace Ledger.Api.Tests;

public sealed class LedgerServiceTests : IAsyncLifetime
{
    private string _directory = null!;
    private string _filePath = null!;
    private LedgerApplicationService _service = null!;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ledger-tests", Guid.NewGuid().ToString("N"));
        _filePath = Path.Combine(_directory, "ledger.json");
        _service = new LedgerApplicationService(new JsonLedgerRepository(_filePath));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Posts_credit_and_debit_and_returns_current_balance()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        await _service.PostAsync(account.Id, "customer-123", new("credit", 10000, "credit-1", "payroll"), default);
        await _service.PostAsync(account.Id, "customer-123", new("debit", 2500, "debit-1", "purchase"), default);

        var balance = await _service.GetBalanceAsync(account.Id, "customer-123", null, default);

        Assert.Equal(7500, balance!.BalanceInCents);
    }

    [Fact]
    public async Task Rejects_debit_that_would_make_balance_negative()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        var error = await Assert.ThrowsAsync<LedgerApplicationException>(() =>
            _service.PostAsync(account.Id, "customer-123", new("debit", 1, "debit-1", null), default));
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public async Task Repeated_idempotency_key_does_not_duplicate_entry()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        var request = new PostTransactionRequest("credit", 200, "same-key", null);
        var first = await _service.PostAsync(account.Id, "customer-123", request, default);
        var second = await _service.PostAsync(account.Id, "customer-123", request, default);
        var state = await ReadStateAsync();

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Equal(first.Transaction.Id, second.Transaction.Id);
        Assert.Single(state.Entries);
    }

    [Fact]
    public async Task Reusing_idempotency_key_with_different_content_returns_conflict()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        await _service.PostAsync(account.Id, "customer-123", new("credit", 200, "same-key", null), default);
        var error = await Assert.ThrowsAsync<LedgerApplicationException>(() =>
            _service.PostAsync(account.Id, "customer-123", new("credit", 300, "same-key", null), default));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal(200, (await _service.GetBalanceAsync(account.Id, "customer-123", null, default))!.BalanceInCents);
    }

    [Fact]
    public async Task Historical_balance_sums_entries_up_to_requested_instant()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        var credit = await _service.PostAsync(account.Id, "customer-123", new("credit", 900, "credit-1", null), default);
        var balance = await _service.GetBalanceAsync(account.Id, "customer-123", credit.Transaction.OccurredAt, default);
        Assert.Equal(900, balance!.BalanceInCents);
    }

    [Fact]
    public async Task State_survives_service_restart()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        await _service.PostAsync(account.Id, "customer-123", new("credit", 400, "credit-1", null), default);
        var restartedService = new LedgerApplicationService(new JsonLedgerRepository(_filePath));
        var balance = await restartedService.GetBalanceAsync(account.Id, "customer-123", null, default);
        Assert.Equal(400, balance!.BalanceInCents);
    }

    [Fact]
    public async Task Concurrent_requests_to_same_instance_preserve_all_credits()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        var requests = Enumerable.Range(0, 10).Select(i =>
            _service.PostAsync(account.Id, "customer-123", new("credit", 100, $"credit-{i}", null), default));

        await Task.WhenAll(requests);

        Assert.Equal(1000, (await _service.GetBalanceAsync(account.Id, "customer-123", null, default))!.BalanceInCents);
    }

    [Fact]
    public async Task Account_is_not_visible_to_another_owner()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);

        var balance = await _service.GetBalanceAsync(account.Id, "customer-456", null, default);
        var error = await Assert.ThrowsAsync<LedgerApplicationException>(() =>
            _service.PostAsync(account.Id, "customer-456", new("credit", 100, "other-owner", null), default));

        Assert.Null(balance);
        Assert.Equal(404, error.StatusCode);
    }

    private async Task<LedgerSnapshot> ReadStateAsync()
    {
        await using var stream = File.OpenRead(_filePath);
        return (await JsonSerializer.DeserializeAsync<LedgerSnapshot>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }))!;
    }
}
