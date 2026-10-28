using Financial.Domain;

namespace Financial.Application;

public sealed class FinancialApplicationService(IFinancialRepository repository, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AccountResponse> CreateAccountAsync(string ownerReference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerReference) || ownerReference.Length > 100)
            throw new FinancialApplicationException("OwnerReference deve ter entre 1 e 100 caracteres.", 400);

        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await repository.LoadAsync(ct);
            var account = Account.Open(ownerReference, timeProvider.GetUtcNow());
            snapshot.Accounts.Add(account);
            await repository.SaveAsync(snapshot, ct);
            return new(account.Id, account.CreatedAt);
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountResponse?> GetAccountAsync(Guid accountId, string ownerReference, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await repository.LoadAsync(ct);
            var account = snapshot.Accounts.SingleOrDefault(x => x.Id == accountId && x.OwnerReference == ownerReference);
            return account is null ? null : new(account.Id, account.CreatedAt);
        }
        finally { _gate.Release(); }
    }

    public async Task<PostResult> PostAsync(Guid accountId, string ownerReference, PostTransactionRequest request, CancellationToken ct)
    {
        Validate(request);
        var type = ParseType(request.Type);
        var amount = new Money(request.AmountInCents);

        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await repository.LoadAsync(ct);
            var accountIndex = snapshot.Accounts.FindIndex(x => x.Id == accountId && x.OwnerReference == ownerReference);
            if (accountIndex < 0) throw new FinancialApplicationException("Conta não encontrada.", 404);

            var existing = snapshot.Entries.SingleOrDefault(x => x.AccountId == accountId && x.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                EnsureSameRequest(existing, type, amount, request.Description);
                return new(ToResponse(existing), false);
            }

            Account updatedAccount;
            try { updatedAccount = snapshot.Accounts[accountIndex].Apply(type, amount); }
            catch (InsufficientFundsException ex) { throw new FinancialApplicationException(ex.Message, 422); }
            catch (DomainRuleException ex) { throw new FinancialApplicationException(ex.Message, 422); }

            var account = snapshot.Accounts[accountIndex];
            var occurredAt = NextOccurrenceAt(snapshot, account);
            var entry = FinancialEntry.Create(accountId, type, amount, request.IdempotencyKey, request.Description, occurredAt);
            snapshot.Accounts[accountIndex] = updatedAccount;
            snapshot.Entries.Add(entry);
            await repository.SaveAsync(snapshot, ct);
            return new(ToResponse(entry), true);
        }
        finally { _gate.Release(); }
    }

    public async Task<BalanceResponse?> GetBalanceAsync(Guid accountId, string ownerReference, DateTimeOffset? at, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await repository.LoadAsync(ct);
            var account = snapshot.Accounts.SingleOrDefault(x => x.Id == accountId && x.OwnerReference == ownerReference);
            if (account is null) return null;

            var lastRecordedAt = GetLastRecordedAt(snapshot, account);
            var now = timeProvider.GetUtcNow();
            var instant = at ?? (now > lastRecordedAt ? now : lastRecordedAt);
            var balance = at is null
                ? account.Balance
                : snapshot.Entries.Where(x => x.AccountId == accountId && x.OccurredAt <= instant)
                    .Aggregate(Money.Zero, (total, entry) => total.Add(entry.SignedAmount));
            return new(accountId, balance.Cents, instant);
        }
        finally { _gate.Release(); }
    }

    public async Task<CustomerBalanceResponse?> GetCustomerBalanceAsync(string ownerReference, DateTimeOffset? at, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await repository.LoadAsync(ct);
            var accounts = snapshot.Accounts.Where(x => x.OwnerReference == ownerReference).ToArray();
            if (accounts.Length == 0) return null;

            var accountIds = accounts.Select(x => x.Id).ToHashSet();
            var entries = snapshot.Entries.Where(x => accountIds.Contains(x.AccountId)).ToArray();
            var lastRecordedAt = entries.Select(x => x.OccurredAt)
                .Concat(accounts.Select(x => x.CreatedAt))
                .Max();
            var now = timeProvider.GetUtcNow();
            var instant = at ?? (now > lastRecordedAt ? now : lastRecordedAt);

            try
            {
                var balance = at is null
                    ? accounts.Aggregate(Money.Zero, (total, account) => total.Add(account.Balance))
                    : entries.Where(x => x.OccurredAt <= instant)
                        .Aggregate(Money.Zero, (total, entry) => total.Add(entry.SignedAmount));
                return new(balance.Cents, instant, accounts.Length);
            }
            catch (DomainRuleException)
            {
                throw new FinancialApplicationException("O saldo consolidado excede o limite suportado.", 422);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<TransactionResponse?> GetTransactionAsync(Guid accountId, Guid transactionId, string ownerReference, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await repository.LoadAsync(ct);
            var ownsAccount = snapshot.Accounts.Any(x => x.Id == accountId && x.OwnerReference == ownerReference);
            if (!ownsAccount) return null;

            var entry = snapshot.Entries.SingleOrDefault(x => x.AccountId == accountId && x.Id == transactionId);
            return entry is null ? null : ToResponse(entry);
        }
        finally { _gate.Release(); }
    }

    private static void Validate(PostTransactionRequest request)
    {
        if (request.AmountInCents <= 0) throw new FinancialApplicationException("AmountInCents deve ser maior que zero.", 400);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
            throw new FinancialApplicationException("IdempotencyKey deve ter entre 1 e 100 caracteres.", 400);
        if (request.Type is not ("credit" or "debit")) throw new FinancialApplicationException("Type deve ser 'credit' ou 'debit'.", 400);
        if (request.Description?.Length > 250) throw new FinancialApplicationException("Description deve ter no máximo 250 caracteres.", 400);
    }

    private static TransactionType ParseType(string type) => type == "credit" ? TransactionType.Credit : TransactionType.Debit;

    private DateTimeOffset NextOccurrenceAt(FinancialSnapshot snapshot, Account account)
    {
        var lastRecordedAt = GetLastRecordedAt(snapshot, account);
        var now = timeProvider.GetUtcNow();
        if (now > lastRecordedAt) return now;

        try { return lastRecordedAt.AddTicks(1); }
        catch (ArgumentOutOfRangeException)
        {
            throw new FinancialApplicationException("Não há um horário representável após o último lançamento.", 422);
        }
    }

    private static DateTimeOffset GetLastRecordedAt(FinancialSnapshot snapshot, Account account) =>
        snapshot.Entries.Where(x => x.AccountId == account.Id)
            .Select(x => x.OccurredAt)
            .DefaultIfEmpty(account.CreatedAt)
            .Max();

    private static void EnsureSameRequest(FinancialEntry entry, TransactionType type, Money amount, string? description)
    {
        if (entry.Type != type || entry.Amount != amount || entry.Description != description)
            throw new FinancialApplicationException("A chave de idempotência já foi usada com outro conteúdo.", 409);
    }

    private static TransactionResponse ToResponse(FinancialEntry entry) =>
        new(entry.Id, entry.AccountId, entry.Type == TransactionType.Credit ? "credit" : "debit",
            entry.Amount.Cents, entry.Description, entry.OccurredAt);
}
