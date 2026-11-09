using Npgsql;
using Xunit;

namespace Financial.Api.Tests;

[Collection(ProcessEnvironmentTestCollection.Name)]
public sealed class PostgresRuntimeRoleTests
{
    private readonly string? _connectionString =
        Environment.GetEnvironmentVariable("FinancialTests__PostgresConnectionString");

    [PostgresFact]
    public async Task Runtime_role_can_write_but_cannot_create_objects_or_delete_ledger_rows()
    {
        Assert.False(string.IsNullOrWhiteSpace(_connectionString));

        var suffix = Guid.NewGuid().ToString("N");
        var migratorRole = $"financial_role_m_{suffix}";
        var runtimeRole = $"financial_role_r_{suffix}";
        var schema = $"financial_role_s_{suffix}";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync($"CREATE ROLE {migratorRole} NOLOGIN", connection, transaction);
        await ExecuteAsync($"CREATE ROLE {runtimeRole} NOLOGIN", connection, transaction);
        await ExecuteAsync($"CREATE SCHEMA {schema} AUTHORIZATION {migratorRole}", connection, transaction);
        await ExecuteAsync($"GRANT USAGE ON SCHEMA {schema} TO {runtimeRole}", connection, transaction);
        await ExecuteAsync(
            $"ALTER DEFAULT PRIVILEGES FOR ROLE {migratorRole} IN SCHEMA {schema} REVOKE ALL PRIVILEGES ON TABLES FROM {runtimeRole}",
            connection, transaction);
        await ExecuteAsync(
            $"ALTER DEFAULT PRIVILEGES FOR ROLE {migratorRole} IN SCHEMA {schema} GRANT SELECT, INSERT, UPDATE ON TABLES TO {runtimeRole}",
            connection, transaction);

        await ExecuteAsync($"SET ROLE {migratorRole}", connection, transaction);
        await ExecuteAsync($"CREATE TABLE {schema}.ledger (id integer PRIMARY KEY, value text NOT NULL)", connection, transaction);
        await ExecuteAsync("RESET ROLE", connection, transaction);
        await ExecuteAsync($"SET ROLE {runtimeRole}", connection, transaction);

        await ExecuteAsync($"INSERT INTO {schema}.ledger VALUES (1, 'created by runtime')", connection, transaction);
        await ExecuteAsync($"UPDATE {schema}.ledger SET value = 'updated by runtime' WHERE id = 1", connection, transaction);

        await using (var privilegeCheck = new NpgsqlCommand(
                         "SELECT NOT has_schema_privilege(current_user, @schema, 'CREATE') " +
                         "AND NOT has_table_privilege(current_user, @table, 'DELETE')",
                         connection, transaction))
        {
            privilegeCheck.Parameters.AddWithValue("schema", schema);
            privilegeCheck.Parameters.AddWithValue("table", $"{schema}.ledger");
            Assert.True((bool)(await privilegeCheck.ExecuteScalarAsync() ?? false));
        }

        await ExecuteAsync($"""
            DO $$
            BEGIN
              BEGIN
                EXECUTE 'CREATE TABLE {schema}.forbidden (id integer)';
                RAISE EXCEPTION 'Runtime role unexpectedly created a table';
              EXCEPTION WHEN insufficient_privilege THEN NULL;
              END;
              BEGIN
                EXECUTE 'DELETE FROM {schema}.ledger';
                RAISE EXCEPTION 'Runtime role unexpectedly deleted a ledger row';
              EXCEPTION WHEN insufficient_privilege THEN NULL;
              END;
            END $$;
            """, connection, transaction);

        await transaction.RollbackAsync();
    }

    private static async Task ExecuteAsync(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
