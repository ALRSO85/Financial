using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Api.Domain;

namespace Ledger.Api.Services;

public sealed class LedgerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LedgerService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _filePath = configuration["Ledger:FilePath"] ?? Path.Combine(environment.ContentRootPath, "data", "ledger.json");
    }

    public LedgerService(string filePath) => _filePath = filePath;

    public async Task<AccountResponse> CreateAccountAsync(string ownerReference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerReference) || ownerReference.Length > 100)
            throw new LedgerException("OwnerReference deve ter entre 1 e 100 caracteres.", 400);

        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            var account = new Account(Guid.NewGuid(), ownerReference.Trim(), DateTimeOffset.UtcNow, 0);
            state.Accounts.Add(account);
            await SaveAsync(state, ct);
            return new(account.Id, account.OwnerReference, account.CreatedAt);
        }
        finally { _gate.Release(); }
    }

    public async Task<PostResult> PostAsync(Guid accountId, PostTransactionRequest request, CancellationToken ct)
    {
        if (request.AmountInCents <= 0) throw new LedgerException("AmountInCents deve ser maior que zero.", 400);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
            throw new LedgerException("IdempotencyKey deve ter entre 1 e 100 caracteres.", 400);
        if (request.Type is not ("credit" or "debit")) throw new LedgerException("Type deve ser 'credit' ou 'debit'.", 400);
        if (request.Description?.Length > 250) throw new LedgerException("Description deve ter no máximo 250 caracteres.", 400);

        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            var existing = state.Entries.SingleOrDefault(x => x.AccountId == accountId && x.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                EnsureSameRequest(existing, request);
                return new(ToResponse(existing), false);
            }

            var index = state.Accounts.FindIndex(x => x.Id == accountId);
            if (index < 0) throw new LedgerException("Conta não encontrada.", 404);
            var account = state.Accounts[index];
            var signedAmount = request.Type == "credit" ? request.AmountInCents : checked(-request.AmountInCents);
            long newBalance;
            try { newBalance = checked(account.BalanceInCents + signedAmount); }
            catch (OverflowException) { throw new LedgerException("Movimentação excede o limite suportado.", 422); }
            if (newBalance < 0) throw new LedgerException("Saldo insuficiente.", 422);

            var entry = new LedgerEntry(Guid.NewGuid(), accountId, request.Type, request.AmountInCents, signedAmount,
                request.IdempotencyKey, request.Description, DateTimeOffset.UtcNow);
            state.Accounts[index] = account with { BalanceInCents = newBalance };
            state.Entries.Add(entry);
            await SaveAsync(state, ct);
            return new(ToResponse(entry), true);
        }
        finally { _gate.Release(); }
    }

    public async Task<BalanceResponse?> GetBalanceAsync(Guid accountId, DateTimeOffset? at, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            var account = state.Accounts.SingleOrDefault(x => x.Id == accountId);
            if (account is null) return null;
            var instant = at ?? DateTimeOffset.UtcNow;
            var balance = at is null
                ? account.BalanceInCents
                : state.Entries.Where(x => x.AccountId == accountId && x.OccurredAt <= instant).Sum(x => x.SignedAmountInCents);
            return new(accountId, balance, instant);
        }
        finally { _gate.Release(); }
    }

    private async Task<LedgerState> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return new([], []);
        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<LedgerState>(stream, JsonOptions, ct)
            ?? throw new InvalidDataException("O arquivo do ledger está vazio ou inválido.");
    }

    private async Task SaveAsync(LedgerState state, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void EnsureSameRequest(LedgerEntry existing, PostTransactionRequest request)
    {
        if (existing.Type != request.Type || existing.AmountInCents != request.AmountInCents || existing.Description != request.Description)
            throw new LedgerException("A chave de idempotência já foi usada com outro conteúdo.", 409);
    }

    private static TransactionResponse ToResponse(LedgerEntry entry) =>
        new(entry.Id, entry.AccountId, entry.Type, entry.AmountInCents, entry.Description, entry.OccurredAt);
}

public sealed class LedgerState(List<Account> accounts, List<LedgerEntry> entries)
{
    public List<Account> Accounts { get; } = accounts;
    public List<LedgerEntry> Entries { get; } = entries;
}

public sealed class LedgerException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
