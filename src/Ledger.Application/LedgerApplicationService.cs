using Ledger.Domain;

namespace Ledger.Application;

public sealed class LedgerApplicationService(ILedgerRepository repository)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AccountResponse> CreateAccountAsync(string ownerReference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerReference) || ownerReference.Length > 100)
            throw new LedgerApplicationException("OwnerReference deve ter entre 1 e 100 caracteres.", 400);

        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await repository.LoadAsync(ct);
            var account = Account.Open(ownerReference.Trim(), DateTimeOffset.UtcNow);
            snapshot.Accounts.Add(account);
            await repository.SaveAsync(snapshot, ct);
            return new(account.Id, account.CreatedAt);
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
            if (accountIndex < 0) throw new LedgerApplicationException("Conta não encontrada.", 404);

            var existing = snapshot.Entries.SingleOrDefault(x => x.AccountId == accountId && x.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                EnsureSameRequest(existing, type, amount, request.Description);
                return new(ToResponse(existing), false);
            }

            Account updatedAccount;
            try { updatedAccount = snapshot.Accounts[accountIndex].Apply(type, amount); }
            catch (InsufficientFundsException ex) { throw new LedgerApplicationException(ex.Message, 422); }
            catch (DomainRuleException ex) { throw new LedgerApplicationException(ex.Message, 422); }

            var entry = LedgerEntry.Create(accountId, type, amount, request.IdempotencyKey, request.Description, DateTimeOffset.UtcNow);
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

            var instant = at ?? DateTimeOffset.UtcNow;
            var balance = at is null
                ? account.Balance
                : snapshot.Entries.Where(x => x.AccountId == accountId && x.OccurredAt <= instant)
                    .Aggregate(Money.Zero, (total, entry) => total.Add(entry.SignedAmount));
            return new(accountId, balance.Cents, instant);
        }
        finally { _gate.Release(); }
    }

    private static void Validate(PostTransactionRequest request)
    {
        if (request.AmountInCents <= 0) throw new LedgerApplicationException("AmountInCents deve ser maior que zero.", 400);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
            throw new LedgerApplicationException("IdempotencyKey deve ter entre 1 e 100 caracteres.", 400);
        if (request.Type is not ("credit" or "debit")) throw new LedgerApplicationException("Type deve ser 'credit' ou 'debit'.", 400);
        if (request.Description?.Length > 250) throw new LedgerApplicationException("Description deve ter no máximo 250 caracteres.", 400);
    }

    private static TransactionType ParseType(string type) => type == "credit" ? TransactionType.Credit : TransactionType.Debit;

    private static void EnsureSameRequest(LedgerEntry entry, TransactionType type, Money amount, string? description)
    {
        if (entry.Type != type || entry.Amount != amount || entry.Description != description)
            throw new LedgerApplicationException("A chave de idempotência já foi usada com outro conteúdo.", 409);
    }

    private static TransactionResponse ToResponse(LedgerEntry entry) =>
        new(entry.Id, entry.AccountId, entry.Type == TransactionType.Credit ? "credit" : "debit",
            entry.Amount.Cents, entry.Description, entry.OccurredAt);
}
