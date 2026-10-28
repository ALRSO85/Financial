namespace Ledger.Api.Domain;

public sealed record Account(Guid Id, string OwnerReference, DateTimeOffset CreatedAt, long BalanceInCents);

public sealed record LedgerEntry(Guid Id, Guid AccountId, string Type, long AmountInCents, long SignedAmountInCents,
    string IdempotencyKey, string? Description, DateTimeOffset OccurredAt);

public sealed record AccountResponse(Guid Id, string OwnerReference, DateTimeOffset CreatedAt);
public sealed record TransactionResponse(Guid Id, Guid AccountId, string Type, long AmountInCents, string? Description, DateTimeOffset OccurredAt);
public sealed record BalanceResponse(Guid AccountId, long BalanceInCents, DateTimeOffset AsOf);
public sealed record PostResult(TransactionResponse Transaction, bool IsNew);
