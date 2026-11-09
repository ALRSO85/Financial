using Financial.Domain;

namespace Financial.Application;

public interface IFinancialRepository
{
    Task<FinancialSnapshot> LoadAsync(CancellationToken cancellationToken);
    Task<TResult> ExecuteWriteAsync<TResult>(
        Func<IFinancialWriteSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken);
}

public interface IFinancialWriteSession
{
    Task AddAccountAsync(Account account, CancellationToken cancellationToken);
    Task<LockedAccountContext?> LockAccountAsync(Guid accountId, string ownerReference, string idempotencyKey,
        CancellationToken cancellationToken);
    Task UpdateAccountAsync(Account account, DateTimeOffset lastRecordedAt, CancellationToken cancellationToken);
    Task AddEntryAsync(FinancialEntry entry, CancellationToken cancellationToken);
    Task RecordAuditAsync(string action, Guid accountId, Guid? transactionId, CancellationToken cancellationToken);
}

public sealed record LockedAccountContext(Account Account, FinancialEntry? ExistingEntry, DateTimeOffset LastRecordedAt);

public interface IFinancialReadRepository
{
    Task<AccountResponse?> GetAccountAsync(Guid accountId, string ownerReference, CancellationToken cancellationToken);
    Task<TransactionResponse?> GetTransactionAsync(Guid accountId, Guid transactionId, string ownerReference, CancellationToken cancellationToken);
    Task<BalanceResponse?> GetBalanceAsync(Guid accountId, string ownerReference, DateTimeOffset? at, CancellationToken cancellationToken);
    Task<CustomerBalanceResponse?> GetCustomerBalanceAsync(string ownerReference, DateTimeOffset? at, CancellationToken cancellationToken);
}
