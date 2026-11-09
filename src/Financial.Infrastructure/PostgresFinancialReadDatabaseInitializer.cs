using Npgsql;

namespace Financial.Infrastructure;

public sealed class PostgresFinancialReadDatabaseInitializer(NpgsqlDataSource dataSource)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await PostgresMigrationRunner.ApplyAsync(dataSource, "002_ReadProjection", 8_416_072_313, cancellationToken);
        await PostgresMigrationRunner.ApplyAsync(dataSource, "004_RemoveRedundantEntryIndexes", 8_416_072_313, cancellationToken);
    }
}
