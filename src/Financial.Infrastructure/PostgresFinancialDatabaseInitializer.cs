using Npgsql;

namespace Financial.Infrastructure;

public sealed class PostgresFinancialDatabaseInitializer(NpgsqlDataSource dataSource)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await PostgresMigrationRunner.ApplyAsync(dataSource, "001_InitialSchema", 8_416_072_312, cancellationToken);
        await PostgresMigrationRunner.ApplyAsync(dataSource, "003_AuditAndDeadLetters", 8_416_072_312, cancellationToken);
    }
}
