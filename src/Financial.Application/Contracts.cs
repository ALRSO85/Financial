namespace Financial.Application;

public sealed record PostTransactionRequest(string Type, long AmountInCents, string IdempotencyKey, string? Description);
public sealed record AccountResponse(Guid Id, DateTimeOffset CreatedAt);
public sealed record TransactionResponse(Guid Id, Guid AccountId, string Type, long AmountInCents, string? Description, DateTimeOffset OccurredAt);
public sealed record BalanceResponse(Guid AccountId, long BalanceInCents, DateTimeOffset AsOf);
public sealed record CustomerBalanceResponse(long BalanceInCents, DateTimeOffset AsOf, int AccountCount);
public sealed record PostResult(TransactionResponse Transaction, bool IsNew);

public sealed class FinancialApplicationException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
