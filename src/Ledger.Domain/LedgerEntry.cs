namespace Ledger.Domain;

public sealed record LedgerEntry(Guid Id, Guid AccountId, TransactionType Type, Money Amount, Money SignedAmount,
    string IdempotencyKey, string? Description, DateTimeOffset OccurredAt)
{
    public static LedgerEntry Create(Guid accountId, TransactionType type, Money amount, string idempotencyKey,
        string? description, DateTimeOffset occurredAt)
    {
        var signedAmount = type == TransactionType.Credit ? amount : new Money(-amount.Cents);
        return new(Guid.NewGuid(), accountId, type, amount, signedAmount, idempotencyKey, description, occurredAt);
    }
}

public sealed record LedgerSnapshot(List<Account> Accounts, List<LedgerEntry> Entries);
