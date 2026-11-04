using Npgsql;

namespace Financial.Infrastructure;

public sealed class PostgresFinancialReadDatabaseInitializer(NpgsqlDataSource dataSource)
{
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        PostgresMigrationRunner.ApplyAsync(dataSource, "002_ReadProjection", 8_416_072_313, cancellationToken);
}
