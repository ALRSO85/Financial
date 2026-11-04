using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Financial.Api;

public sealed class PostgresReadinessHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL está acessível.");
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL está indisponível.", exception);
        }
    }
}

public sealed class ProjectionFreshnessHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    private static readonly TimeSpan DegradedAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleHeartbeatAfter = TimeSpan.FromSeconds(90);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT worker_heartbeat_at, pending_count, oldest_pending_at, dead_letter_count " +
                "FROM financial_projection_status WHERE status_id = 1",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return HealthCheckResult.Degraded("O projetor ainda não publicou seu heartbeat.");

            var heartbeatAt = reader.GetFieldValue<DateTimeOffset>(0);
            var pendingCount = reader.GetInt64(1);
            var oldestPendingAt = reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(2);
            var deadLetterCount = reader.GetInt64(3);
            var heartbeatAge = (DateTimeOffset.UtcNow - heartbeatAt).TotalSeconds;
            var pendingAge = oldestPendingAt is null ? 0 : (DateTimeOffset.UtcNow - oldestPendingAt.Value).TotalSeconds;
            var data = new Dictionary<string, object>
            {
                ["projectorHeartbeatAgeSeconds"] = Math.Max(0, heartbeatAge),
                ["pendingEvents"] = pendingCount,
                ["oldestPendingAgeSeconds"] = Math.Max(0, pendingAge),
                ["deadLetterEvents"] = deadLetterCount
            };
            var degraded = heartbeatAge > StaleHeartbeatAfter.TotalSeconds || pendingAge > DegradedAfter.TotalSeconds || deadLetterCount > 0;
            return degraded
                ? HealthCheckResult.Degraded("A projeção está degradada; as consultas continuam disponíveis com dataAsOf.", data: data)
                : HealthCheckResult.Healthy("Projetor ativo e sem backlog acima do limite operacional.", data);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("Não foi possível consultar o estado da projeção.", exception);
        }
    }
}
