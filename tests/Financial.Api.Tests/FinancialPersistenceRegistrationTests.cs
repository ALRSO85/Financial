using Financial.Api;
using Financial.Application;
using Financial.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Financial.Api.Tests;

public sealed class FinancialPersistenceRegistrationTests
{
    [Fact]
    public void Json_provider_remains_available_for_isolated_prototype_tests()
    {
        var builder = CreateBuilder();
        builder.Configuration["Financial:StorageProvider"] = "json";
        builder.Configuration["Financial:FilePath"] = Path.Combine(Path.GetTempPath(), "financial-registration-test.json");

        builder.Services.AddFinancialPersistence(builder.Configuration, builder.Environment, "combined");
        using var serviceProvider = builder.Services.BuildServiceProvider();

        Assert.IsType<JsonFinancialRepository>(serviceProvider.GetRequiredService<IFinancialRepository>());
    }

    [Fact]
    public void Postgres_provider_registers_the_transactional_repository()
    {
        var builder = CreateBuilder();
        builder.Configuration["Financial:StorageProvider"] = "postgres";
        builder.Configuration["ConnectionStrings:FinancialWrite"] = "Host=localhost;Database=financial;Username=financial;Password=financial";

        builder.Services.AddFinancialPersistence(builder.Configuration, builder.Environment, "write");
        using var serviceProvider = builder.Services.BuildServiceProvider();

        Assert.IsType<PostgresFinancialRepository>(serviceProvider.GetRequiredService<IFinancialRepository>());
        Assert.NotNull(serviceProvider.GetRequiredService<PostgresFinancialDatabaseInitializer>());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("unknown")]
    public void Unsupported_persistence_provider_fails_during_registration(string provider)
    {
        var builder = CreateBuilder();
        builder.Configuration["Financial:StorageProvider"] = provider;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddFinancialPersistence(builder.Configuration, builder.Environment, "write"));

        Assert.Contains(provider, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplicationBuilder CreateBuilder() =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = Path.GetTempPath()
        });
}
