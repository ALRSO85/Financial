using Financial.Domain;

namespace Financial.Application;

public interface IFinancialRepository
{
    Task<FinancialSnapshot> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(FinancialSnapshot snapshot, CancellationToken cancellationToken);
}
