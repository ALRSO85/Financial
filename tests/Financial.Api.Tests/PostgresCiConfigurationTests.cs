using Xunit;

namespace Financial.Api.Tests;

public sealed class PostgresCiConfigurationTests
{
    [Fact]
    public void GitHub_actions_provides_distinct_write_and_read_postgres_connections()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var writeConnection = Environment.GetEnvironmentVariable("FinancialTests__PostgresConnectionString");
        var readConnection = Environment.GetEnvironmentVariable("FinancialTests__ReadPostgresConnectionString");

        Assert.False(string.IsNullOrWhiteSpace(writeConnection),
            "The PostgreSQL write connection must be configured in GitHub Actions.");
        Assert.False(string.IsNullOrWhiteSpace(readConnection),
            "The PostgreSQL read connection must be configured in GitHub Actions.");
        Assert.NotEqual(writeConnection, readConnection);
    }
}
