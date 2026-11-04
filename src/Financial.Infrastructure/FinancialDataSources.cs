using Npgsql;

namespace Financial.Infrastructure;

public sealed class FinancialDataSources(NpgsqlDataSource write, NpgsqlDataSource read) : IAsyncDisposable
{
    public NpgsqlDataSource Write { get; } = write;
    public NpgsqlDataSource Read { get; } = read;

    public async ValueTask DisposeAsync()
    {
        await Write.DisposeAsync();
        await Read.DisposeAsync();
    }
}
