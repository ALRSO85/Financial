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
    Task<Account?> GetAccountForUpdateAsync(Guid accountId, string ownerReference, CancellationToken cancellationToken);
    Task<FinancialEntry?> FindByIdempotencyKeyAsync(Guid accountId, string idempotencyKey, CancellationToken cancellationToken);
    Task<DateTimeOffset> GetLastRecordedAtAsync(Guid accountId, DateTimeOffset createdAt, CancellationToken cancellationToken);
    Task UpdateAccountAsync(Account account, CancellationToken cancellationToken);
    Task AddEntryAsync(FinancialEntry entry, CancellationToken cancellationToken);
    Task RecordAuditAsync(string action, Guid accountId, Guid? transactionId, CancellationToken cancellationToken);
}

public interface IFinancialReadRepository
{
    Task<AccountResponse?> GetAccountAsync(Guid accountId, string ownerReference, CancellationToken cancellationToken);
    Task<TransactionResponse?> GetTransactionAsync(Guid accountId, Guid transactionId, string ownerReference, CancellationToken cancellationToken);
    Task<BalanceResponse?> GetBalanceAsync(Guid accountId, string ownerReference, DateTimeOffset? at, CancellationToken cancellationToken);
    Task<CustomerBalanceResponse?> GetCustomerBalanceAsync(string ownerReference, DateTimeOffset? at, CancellationToken cancellationToken);
}
