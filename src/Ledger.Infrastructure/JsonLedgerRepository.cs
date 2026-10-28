using System.Text.Json;
using Ledger.Application;
using Ledger.Domain;

namespace Ledger.Infrastructure;

public sealed class JsonLedgerRepository(string filePath) : ILedgerRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<LedgerSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return new([], []);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<LedgerSnapshot>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("O arquivo do ledger está vazio ou inválido.");
    }

    public async Task SaveAsync(LedgerSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
