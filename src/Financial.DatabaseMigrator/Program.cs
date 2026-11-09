using Financial.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

var target = args.SingleOrDefault()?.ToLowerInvariant();
if (target is not ("write" or "read" or "all"))
    throw new ArgumentException("Informe um alvo: write, read ou all.");

var builder = Host.CreateApplicationBuilder();
var environment = builder.Environment;
if (target is "write" or "all")
{
    var connectionString = builder.Configuration.GetConnectionString("FinancialWriteMigrator")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:FinancialWriteMigrator.");
    ValidateTls(connectionString, environment.IsDevelopment());
    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    await new PostgresFinancialDatabaseInitializer(dataSource).InitializeAsync(CancellationToken.None);
    Console.WriteLine("Migrations do banco de escrita aplicadas.");
}

if (target is "read" or "all")
{
    var connectionString = builder.Configuration.GetConnectionString("FinancialReadMigrator")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:FinancialReadMigrator.");
    ValidateTls(connectionString, environment.IsDevelopment());
    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    await new PostgresFinancialReadDatabaseInitializer(dataSource).InitializeAsync(CancellationToken.None);
    Console.WriteLine("Migrations do banco de leitura aplicadas.");
}

static void ValidateTls(string connectionString, bool isDevelopment)
{
    if (!isDevelopment && new NpgsqlConnectionStringBuilder(connectionString).SslMode != SslMode.VerifyFull)
        throw new InvalidOperationException("Use SslMode=VerifyFull para migrações fora de Development.");
}
