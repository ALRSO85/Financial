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
    public void Json_is_the_default_persistence_provider()
    {
        var builder = CreateBuilder();
        builder.Configuration["Financial:FilePath"] = Path.Combine(Path.GetTempPath(), "financial-registration-test.json");

        builder.Services.AddFinancialPersistence(builder.Configuration, builder.Environment);
        using var serviceProvider = builder.Services.BuildServiceProvider();

        Assert.IsType<JsonFinancialRepository>(serviceProvider.GetRequiredService<IFinancialRepository>());
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlite")]
    public void Unsupported_persistence_provider_fails_during_registration(string provider)
    {
        var builder = CreateBuilder();
        builder.Configuration["Financial:StorageProvider"] = provider;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddFinancialPersistence(builder.Configuration, builder.Environment));

        Assert.Contains(provider, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplicationBuilder CreateBuilder() =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = Path.GetTempPath()
        });
}
