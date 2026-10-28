using System.Text.Json;
using System.Text.Json.Nodes;
using Financial.Application;
using Financial.Domain;
using Financial.Infrastructure;
using Xunit;

namespace Financial.Api.Tests;

public sealed class FinancialApplicationServiceTests : IAsyncLifetime
{
    private string _directory = null!;
    private string _filePath = null!;
    private FinancialApplicationService _service = null!;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "financial-tests", Guid.NewGuid().ToString("N"));
        _filePath = Path.Combine(_directory, "financial.json");
        _service = new FinancialApplicationService(new JsonFinancialRepository(_filePath), TimeProvider.System);
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
        var error = await Assert.ThrowsAsync<FinancialApplicationException>(() =>
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
        var error = await Assert.ThrowsAsync<FinancialApplicationException>(() =>
            _service.PostAsync(account.Id, "customer-123", new("credit", 300, "same-key", null), default));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal(200, (await _service.GetBalanceAsync(account.Id, "customer-123", null, default))!.BalanceInCents);
    }

    [Fact]
    public async Task Historical_balance_sums_entries_up_to_requested_instant()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        var credit = await _service.PostAsync(account.Id, "customer-123", new("credit", 900, "credit-1", null), default);
        var debit = await _service.PostAsync(account.Id, "customer-123", new("debit", 250, "debit-1", null), default);

        var beforeCredit = await _service.GetBalanceAsync(account.Id, "customer-123", credit.Transaction.OccurredAt.AddTicks(-1), default);
        var afterCredit = await _service.GetBalanceAsync(account.Id, "customer-123", credit.Transaction.OccurredAt, default);
        var afterDebit = await _service.GetBalanceAsync(account.Id, "customer-123", debit.Transaction.OccurredAt, default);

        Assert.Equal(0, beforeCredit!.BalanceInCents);
        Assert.Equal(900, afterCredit!.BalanceInCents);
        Assert.Equal(650, afterDebit!.BalanceInCents);
    }

    [Fact]
    public async Task Customer_balance_consolidates_accounts_and_supports_historical_instant()
    {
        var initialTime = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(initialTime);
        var service = new FinancialApplicationService(new JsonFinancialRepository(_filePath), clock);
        var firstAccount = await service.CreateAccountAsync("customer-123", default);
        var secondAccount = await service.CreateAccountAsync("customer-123", default);

        clock.SetUtcNow(initialTime.AddMinutes(1));
        var firstCredit = await service.PostAsync(firstAccount.Id, "customer-123", new("credit", 100, "account-one-credit", null), default);
        clock.SetUtcNow(initialTime.AddMinutes(2));
        var secondCredit = await service.PostAsync(secondAccount.Id, "customer-123", new("credit", 200, "account-two-credit", null), default);
        clock.SetUtcNow(initialTime.AddMinutes(3));
        await service.PostAsync(firstAccount.Id, "customer-123", new("debit", 50, "account-one-debit", null), default);

        var afterFirstCredit = await service.GetCustomerBalanceAsync("customer-123", firstCredit.Transaction.OccurredAt, default);
        var afterBothCredits = await service.GetCustomerBalanceAsync("customer-123", secondCredit.Transaction.OccurredAt, default);
        var current = await service.GetCustomerBalanceAsync("customer-123", null, default);

        Assert.Equal(100, afterFirstCredit!.BalanceInCents);
        Assert.Equal(2, afterFirstCredit.AccountCount);
        Assert.Equal(300, afterBothCredits!.BalanceInCents);
        Assert.Equal(250, current!.BalanceInCents);
        Assert.Equal(2, current.AccountCount);
    }

    [Fact]
    public async Task Recorded_times_remain_monotonic_when_system_clock_moves_backwards()
    {
        var initialTime = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(initialTime);
        var service = new FinancialApplicationService(new JsonFinancialRepository(_filePath), clock);
        var account = await service.CreateAccountAsync("customer-123", default);

        clock.SetUtcNow(initialTime.AddMinutes(10));
        var credit = await service.PostAsync(account.Id, "customer-123", new("credit", 900, "credit-clock", null), default);
        clock.SetUtcNow(initialTime.AddMinutes(5));
        var debit = await service.PostAsync(account.Id, "customer-123", new("debit", 250, "debit-clock", null), default);

        var afterCredit = await service.GetBalanceAsync(account.Id, "customer-123", credit.Transaction.OccurredAt, default);
        var afterDebit = await service.GetBalanceAsync(account.Id, "customer-123", debit.Transaction.OccurredAt, default);
        var current = await service.GetBalanceAsync(account.Id, "customer-123", null, default);

        Assert.True(debit.Transaction.OccurredAt > credit.Transaction.OccurredAt);
        Assert.Equal(900, afterCredit!.BalanceInCents);
        Assert.Equal(650, afterDebit!.BalanceInCents);
        Assert.Equal(650, current!.BalanceInCents);
        Assert.Equal(debit.Transaction.OccurredAt, current.AsOf);
    }

    [Fact]
    public async Task State_survives_service_restart()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        await _service.PostAsync(account.Id, "customer-123", new("credit", 400, "credit-1", null), default);
        var restartedService = new FinancialApplicationService(new JsonFinancialRepository(_filePath), TimeProvider.System);
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
    public async Task Concurrent_retries_with_same_idempotency_key_create_one_entry()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        var request = new PostTransactionRequest("credit", 750, "concurrent-retry", "payment");
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            _service.PostAsync(account.Id, "customer-123", request, default)));
        var state = await ReadStateAsync();

        Assert.Single(state.Entries);
        Assert.Equal(750, (await _service.GetBalanceAsync(account.Id, "customer-123", null, default))!.BalanceInCents);
        Assert.Single(results.Select(x => x.Transaction.Id).Distinct());
        Assert.Single(results, x => x.IsNew);
        Assert.Equal(9, results.Count(x => !x.IsNew));
    }

    [Fact]
    public async Task Account_is_not_visible_to_another_owner()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);

        var balance = await _service.GetBalanceAsync(account.Id, "customer-456", null, default);
        var error = await Assert.ThrowsAsync<FinancialApplicationException>(() =>
            _service.PostAsync(account.Id, "customer-456", new("credit", 100, "other-owner", null), default));

        Assert.Null(balance);
        Assert.Equal(404, error.StatusCode);
    }

    [Fact]
    public async Task Owner_reference_is_preserved_without_normalization()
    {
        const string ownerReference = " customer-123 ";
        var account = await _service.CreateAccountAsync(ownerReference, default);

        var exactOwner = await _service.GetAccountAsync(account.Id, ownerReference, default);
        var normalizedOwner = await _service.GetAccountAsync(account.Id, ownerReference.Trim(), default);

        Assert.NotNull(exactOwner);
        Assert.Null(normalizedOwner);
    }

    [Fact]
    public async Task Corrupt_json_is_rejected_without_overwriting_the_file()
    {
        const string corruptJson = "{ invalid ledger data";
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(_filePath, corruptJson);

        await Assert.ThrowsAsync<JsonException>(() => _service.CreateAccountAsync("customer-123", default));

        Assert.Equal(corruptJson, await File.ReadAllTextAsync(_filePath));
    }

    [Fact]
    public async Task Incomplete_ledger_snapshot_is_rejected_without_overwriting_the_file()
    {
        const string incompleteJson = "{\"accounts\":[]}";
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(_filePath, incompleteJson);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.GetBalanceAsync(Guid.NewGuid(), "customer-123", null, default));

        Assert.Equal(incompleteJson, await File.ReadAllTextAsync(_filePath));
    }

    [Fact]
    public async Task Divergent_materialized_balance_is_rejected_without_overwriting_the_file()
    {
        var account = await _service.CreateAccountAsync("customer-123", default);
        await _service.PostAsync(account.Id, "customer-123", new("credit", 500, "credit-reconcile", null), default);
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(_filePath))!;
        snapshot["accounts"]![0]!["balance"]!["cents"] = 600;
        var inconsistentJson = snapshot.ToJsonString();
        await File.WriteAllTextAsync(_filePath, inconsistentJson);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.GetBalanceAsync(account.Id, "customer-123", null, default));

        Assert.Equal(inconsistentJson, await File.ReadAllTextAsync(_filePath));
    }

    private async Task<FinancialSnapshot> ReadStateAsync()
    {
        await using var stream = File.OpenRead(_filePath);
        return (await JsonSerializer.DeserializeAsync<FinancialSnapshot>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }))!;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
