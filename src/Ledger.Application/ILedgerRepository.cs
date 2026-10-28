using Ledger.Domain;

namespace Ledger.Application;

public interface ILedgerRepository
{
    Task<LedgerSnapshot> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(LedgerSnapshot snapshot, CancellationToken cancellationToken);
}
