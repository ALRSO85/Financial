namespace Financial.Domain;

public sealed record FinancialEntry(Guid Id, Guid AccountId, TransactionType Type, Money Amount, Money SignedAmount,
    string IdempotencyKey, string? Description, DateTimeOffset OccurredAt)
{
    public static FinancialEntry Create(Guid accountId, TransactionType type, Money amount, string idempotencyKey,
        string? description, DateTimeOffset occurredAt)
    {
        var signedAmount = type == TransactionType.Credit ? amount : new Money(-amount.Cents);
        return new(Guid.NewGuid(), accountId, type, amount, signedAmount, idempotencyKey, description, occurredAt);
    }
}

public sealed record FinancialSnapshot(List<Account> Accounts, List<FinancialEntry> Entries);
