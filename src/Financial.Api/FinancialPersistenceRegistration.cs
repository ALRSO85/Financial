using Financial.Application;
using Financial.Infrastructure;
using Npgsql;

namespace Financial.Api;

public static class FinancialPersistenceRegistration
{
    public static IServiceCollection AddFinancialPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string apiRole)
    {
        var provider = configuration["Financial:StorageProvider"] ?? "postgres";
        if (string.Equals(provider, "json", StringComparison.OrdinalIgnoreCase))
        {
            if (apiRole != "combined")
                throw new InvalidOperationException("O adapter JSON só pode ser usado no modo combined de desenvolvimento/teste.");
            var filePath = configuration["Financial:FilePath"] ??
                Path.Combine(environment.ContentRootPath, "data", "financial.json");
            services.AddSingleton<IFinancialRepository>(_ => new JsonFinancialRepository(filePath));
            return services;
        }

        if (!string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"O adapter de persistência '{provider}' não está disponível. Providers suportados: 'postgres' e 'json' (somente protótipo). ");

        var connectionName = apiRole == "read" ? "FinancialRead" : "FinancialWrite";
        var connectionString = configuration.GetConnectionString(connectionName) ??
            (apiRole == "combined" ? configuration["Financial:ConnectionString"] : null);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Configure ConnectionStrings:{connectionName} para usar PostgreSQL.");

        if (!environment.IsDevelopment() && new NpgsqlConnectionStringBuilder(connectionString).SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException("Use SslMode=VerifyFull para conexões PostgreSQL fora de Development.");

        var dataSource = NpgsqlDataSource.Create(connectionString);
        services.AddSingleton(dataSource);
        if (apiRole == "read")
        {
            services.AddSingleton<IFinancialReadRepository, PostgresFinancialReadRepository>();
            services.AddSingleton<PostgresFinancialReadDatabaseInitializer>();
        }
        else
        {
            services.AddSingleton<IFinancialRepository, PostgresFinancialRepository>();
            services.AddSingleton<PostgresFinancialDatabaseInitializer>();
        }
        return services;
    }
}
