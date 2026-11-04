using Financial.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var writeConnectionString = builder.Configuration.GetConnectionString("FinancialWrite")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:FinancialWrite.");
var readConnectionString = builder.Configuration.GetConnectionString("FinancialRead")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:FinancialRead.");
if (!builder.Environment.IsDevelopment() &&
    (new NpgsqlConnectionStringBuilder(writeConnectionString).SslMode != SslMode.VerifyFull ||
     new NpgsqlConnectionStringBuilder(readConnectionString).SslMode != SslMode.VerifyFull))
    throw new InvalidOperationException("Use SslMode=VerifyFull nas duas conexões fora de Development.");

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddSingleton(_ => new FinancialDataSources(
    NpgsqlDataSource.Create(writeConnectionString), NpgsqlDataSource.Create(readConnectionString)));
builder.Services.AddSingleton(serviceProvider =>
{
    var dataSources = serviceProvider.GetRequiredService<FinancialDataSources>();
    return new PostgresOutboxProjector(dataSources.Write, dataSources.Read);
});
builder.Services.AddHostedService<ProjectionWorker>();

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Financial.Projector"))
    .WithTracing(tracing => tracing.AddSource("Financial.Infrastructure"))
    .WithMetrics(metrics => metrics.AddMeter("Financial.Infrastructure"));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    telemetry.WithTracing(tracing => tracing.AddOtlpExporter())
        .WithMetrics(metrics => metrics.AddOtlpExporter())
        .WithLogging(logging => logging.AddOtlpExporter());

await builder.Build().RunAsync();

internal sealed class ProjectionWorker(
    PostgresOutboxProjector projector,
    ILogger<ProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Financial outbox projector started");
        var lastStatusRefresh = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastStatusRefresh >= TimeSpan.FromSeconds(15))
                {
                    await projector.RefreshProjectionStatusAsync(stoppingToken);
                    lastStatusRefresh = DateTimeOffset.UtcNow;
                }
                var projected = await projector.ProjectBatchAsync(stoppingToken);
                if (projected == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                else
                    logger.LogInformation("Projected {EventCount} financial events", projected);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (exception is NpgsqlException { IsTransient: true })
                {
                    logger.LogWarning("Transient dependency failure in projection worker: {ErrorType}", exception.GetType().Name);
                }
                else
                {
                    try
                    {
                        var update = await projector.RecordPermanentFailureAsync(exception.GetType().Name, stoppingToken);
                        if (update?.DeadLetteredAt is not null)
                            logger.LogCritical("Outbox event moved to dead-letter state after {AttemptCount} attempts", update.Attempts);
                        else
                            logger.LogError("Projection event failed with {ErrorType}; attempt {AttemptCount}",
                                exception.GetType().Name, update?.Attempts);
                    }
                    catch (Exception failureStateException)
                    {
                        logger.LogError("Could not persist projection failure state; error type {ErrorType}",
                            failureStateException.GetType().Name);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("Financial outbox projector stopped");
    }
}
