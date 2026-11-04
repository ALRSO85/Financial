using Xunit;

namespace Financial.Api.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FinancialTests__PostgresConnectionString")))
            Skip = "Configure FinancialTests__PostgresConnectionString para executar os testes PostgreSQL.";
    }
}
