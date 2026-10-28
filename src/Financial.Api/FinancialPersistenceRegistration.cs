using Financial.Application;
using Financial.Infrastructure;

namespace Financial.Api;

public static class FinancialPersistenceRegistration
{
    public static IServiceCollection AddFinancialPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var provider = configuration["Financial:StorageProvider"] ?? "json";
        if (!string.Equals(provider, "json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"O adapter de persistência '{provider}' não está disponível. O protótipo suporta apenas 'json'.");

        var filePath = configuration["Financial:FilePath"] ??
            Path.Combine(environment.ContentRootPath, "data", "financial.json");

        services.AddSingleton<IFinancialRepository>(_ => new JsonFinancialRepository(filePath));
        return services;
    }
}
