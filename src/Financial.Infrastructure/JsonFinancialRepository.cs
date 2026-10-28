using System.Text.Json;
using Financial.Application;
using Financial.Domain;

namespace Financial.Infrastructure;

public sealed class JsonFinancialRepository(string filePath) : IFinancialRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<FinancialSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return new([], []);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var snapshot = await JsonSerializer.DeserializeAsync<FinancialSnapshot>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("O arquivo do ledger está vazio ou inválido.");
        ValidateSnapshot(snapshot);
        return snapshot;
    }

    public async Task SaveAsync(FinancialSnapshot snapshot, CancellationToken cancellationToken)
    {
        ValidateSnapshot(snapshot);
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

    private static void ValidateSnapshot(FinancialSnapshot snapshot)
    {
        if (snapshot.Accounts is null || snapshot.Entries is null)
            throw new InvalidDataException("O ledger precisa conter as coleções de contas e lançamentos.");

        var accountsById = new Dictionary<Guid, Account>();
        var calculatedBalances = new Dictionary<Guid, Money>();
        var lastEntryTimes = new Dictionary<Guid, DateTimeOffset>();
        foreach (var account in snapshot.Accounts)
        {
            if (account is null || account.Id == Guid.Empty || string.IsNullOrWhiteSpace(account.OwnerReference) ||
                account.OwnerReference.Length > 100 || account.CreatedAt == default || account.Balance.Cents < 0 ||
                !accountsById.TryAdd(account.Id, account))
                throw new InvalidDataException("O ledger contém uma conta inválida ou duplicada.");

            calculatedBalances.Add(account.Id, Money.Zero);
            lastEntryTimes.Add(account.Id, account.CreatedAt);
        }

        var entryIds = new HashSet<Guid>();
        var idempotencyKeys = new HashSet<(Guid AccountId, string Key)>();
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null || entry.Id == Guid.Empty || !entryIds.Add(entry.Id) ||
                !accountsById.ContainsKey(entry.AccountId) || entry.Amount.Cents <= 0 ||
                string.IsNullOrWhiteSpace(entry.IdempotencyKey) || entry.IdempotencyKey.Length > 100 ||
                entry.Description?.Length > 250 || !idempotencyKeys.Add((entry.AccountId, entry.IdempotencyKey)))
                throw new InvalidDataException("O ledger contém um lançamento inválido, duplicado ou sem conta.");

            var expectedSignedAmount = entry.Type switch
            {
                TransactionType.Credit => entry.Amount,
                TransactionType.Debit => new Money(-entry.Amount.Cents),
                _ => throw new InvalidDataException("O ledger contém um tipo de lançamento desconhecido.")
            };
            if (entry.SignedAmount != expectedSignedAmount || entry.OccurredAt <= lastEntryTimes[entry.AccountId])
                throw new InvalidDataException("O valor assinado ou a ordem temporal do ledger é inconsistente.");

            try
            {
                calculatedBalances[entry.AccountId] = calculatedBalances[entry.AccountId].Add(entry.SignedAmount);
            }
            catch (DomainRuleException ex)
            {
                throw new InvalidDataException("O saldo calculado do ledger excede o limite suportado.", ex);
            }

            if (calculatedBalances[entry.AccountId].Cents < 0)
                throw new InvalidDataException("O ledger contém um saldo negativo.");
            lastEntryTimes[entry.AccountId] = entry.OccurredAt;
        }

        foreach (var account in snapshot.Accounts)
        {
            if (account.Balance != calculatedBalances[account.Id])
                throw new InvalidDataException("O saldo materializado diverge dos lançamentos do ledger.");
        }
    }
}
