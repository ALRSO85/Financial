using System.Reflection;
using Npgsql;

namespace Financial.Infrastructure;

internal static class PostgresMigrationRunner
{
    public static async Task ApplyAsync(NpgsqlDataSource dataSource, string version, long lockId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var bootstrap = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS financial_schema_migrations (version varchar(100) PRIMARY KEY, applied_at timestamptz NOT NULL)",
            connection))
            await bootstrap.ExecuteNonQueryAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@id)", connection, transaction))
        {
            migrationLock.Parameters.AddWithValue("id", lockId);
            await migrationLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM financial_schema_migrations WHERE version = @version)", connection, transaction))
        {
            check.Parameters.AddWithValue("version", version);
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        var resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith($"Migrations.{version}.sql", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"A migration SQL '{version}' não foi incluída no assembly.");
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Não foi possível abrir a migration SQL '{version}'.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        await using (var migration = new NpgsqlCommand(sql, connection, transaction))
            await migration.ExecuteNonQueryAsync(cancellationToken);
        await using (var record = new NpgsqlCommand(
            "INSERT INTO financial_schema_migrations (version, applied_at) VALUES (@version, @appliedAt)", connection, transaction))
        {
            record.Parameters.AddWithValue("version", version);
            record.Parameters.AddWithValue("appliedAt", DateTimeOffset.UtcNow);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
