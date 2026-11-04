using Financial.Domain;

namespace Financial.Application;

public sealed class FinancialApplicationService(IFinancialRepository repository, TimeProvider timeProvider)
{
    public async Task<AccountResponse> CreateAccountAsync(string ownerReference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerReference) || ownerReference.Length > 100)
            throw new FinancialApplicationException("OwnerReference deve ter entre 1 e 100 caracteres.", 400);

        return await repository.ExecuteWriteAsync<AccountResponse>(async (session, token) =>
        {
            var account = Account.Open(ownerReference, NormalizeToDatabasePrecision(timeProvider.GetUtcNow()));
            await session.AddAccountAsync(account, token);
            return new AccountResponse(account.Id, account.CreatedAt);
        }, ct);
    }

    public async Task<AccountResponse?> GetAccountAsync(Guid accountId, string ownerReference, CancellationToken ct)
    {
        var snapshot = await repository.LoadAsync(ct);
        var account = snapshot.Accounts.SingleOrDefault(x => x.Id == accountId && x.OwnerReference == ownerReference);
        return account is null ? null : new(account.Id, account.CreatedAt);
    }

    public async Task<PostResult> PostAsync(Guid accountId, string ownerReference, PostTransactionRequest request, CancellationToken ct)
    {
        Validate(request);
        var type = ParseType(request.Type);
        var amount = new Money(request.AmountInCents);
        using var activity = FinancialTelemetry.ActivitySource.StartActivity("financial.transaction.post");
        activity?.SetTag("financial.transaction.type", request.Type);

        var result = await repository.ExecuteWriteAsync<PostResult>(async (session, token) =>
        {
            var account = await session.GetAccountForUpdateAsync(accountId, ownerReference, token);
            if (account is null) throw new FinancialApplicationException("Conta não encontrada.", 404);

            var existing = await session.FindByIdempotencyKeyAsync(accountId, request.IdempotencyKey, token);
            if (existing is not null)
            {
                EnsureSameRequest(existing, type, amount, request.Description);
                await session.RecordAuditAsync("transaction.idempotent_replay", accountId, existing.Id, token);
                return new(ToResponse(existing), false);
            }

            Account updatedAccount;
            try { updatedAccount = account.Apply(type, amount); }
            catch (InsufficientFundsException ex) { throw new FinancialApplicationException(ex.Message, 422); }
            catch (DomainRuleException ex) { throw new FinancialApplicationException(ex.Message, 422); }

            var lastRecordedAt = await session.GetLastRecordedAtAsync(accountId, account.CreatedAt, token);
            var occurredAt = NextOccurrenceAt(lastRecordedAt);
            var entry = FinancialEntry.Create(accountId, type, amount, request.IdempotencyKey, request.Description, occurredAt);
            await session.UpdateAccountAsync(updatedAccount, token);
            await session.AddEntryAsync(entry, token);
            return new(ToResponse(entry), true);
        }, ct);
        activity?.SetTag("financial.result", result.IsNew ? "created" : "idempotent_replay");
        if (result.IsNew)
            FinancialTelemetry.TransactionsAccepted.Add(1, new KeyValuePair<string, object?>("transaction.type", request.Type));
        else
            FinancialTelemetry.IdempotentReplays.Add(1);
        return result;
    }

    public async Task<BalanceResponse?> GetBalanceAsync(Guid accountId, string ownerReference, DateTimeOffset? at, CancellationToken ct)
    {
        var snapshot = await repository.LoadAsync(ct);
        var account = snapshot.Accounts.SingleOrDefault(x => x.Id == accountId && x.OwnerReference == ownerReference);
        if (account is null) return null;

        var lastRecordedAt = snapshot.Entries.Where(x => x.AccountId == accountId)
            .Select(x => x.OccurredAt).DefaultIfEmpty(account.CreatedAt).Max();
        var now = timeProvider.GetUtcNow();
        var instant = at ?? (now > lastRecordedAt ? now : lastRecordedAt);
        var balance = at is null
            ? account.Balance
            : snapshot.Entries.Where(x => x.AccountId == accountId && x.OccurredAt <= instant)
                .Aggregate(Money.Zero, (total, entry) => total.Add(entry.SignedAmount));
        return new(accountId, balance.Cents, instant);
    }

    public async Task<CustomerBalanceResponse?> GetCustomerBalanceAsync(string ownerReference, DateTimeOffset? at, CancellationToken ct)
    {
        var snapshot = await repository.LoadAsync(ct);
        var accounts = snapshot.Accounts.Where(x => x.OwnerReference == ownerReference).ToArray();
        if (accounts.Length == 0) return null;

        var accountIds = accounts.Select(x => x.Id).ToHashSet();
        var entries = snapshot.Entries.Where(x => accountIds.Contains(x.AccountId)).ToArray();
        var lastRecordedAt = entries.Select(x => x.OccurredAt).Concat(accounts.Select(x => x.CreatedAt)).Max();
        var now = timeProvider.GetUtcNow();
        var instant = at ?? (now > lastRecordedAt ? now : lastRecordedAt);

        try
        {
            var balance = at is null
                ? accounts.Aggregate(Money.Zero, (total, account) => total.Add(account.Balance))
                : entries.Where(x => x.OccurredAt <= instant)
                    .Aggregate(Money.Zero, (total, entry) => total.Add(entry.SignedAmount));
            return new(balance.Cents, instant, accounts.Length);
        }
        catch (DomainRuleException)
        {
            throw new FinancialApplicationException("O saldo consolidado excede o limite suportado.", 422);
        }
    }

    public async Task<TransactionResponse?> GetTransactionAsync(Guid accountId, Guid transactionId, string ownerReference, CancellationToken ct)
    {
        var snapshot = await repository.LoadAsync(ct);
        var ownsAccount = snapshot.Accounts.Any(x => x.Id == accountId && x.OwnerReference == ownerReference);
        if (!ownsAccount) return null;

        var entry = snapshot.Entries.SingleOrDefault(x => x.AccountId == accountId && x.Id == transactionId);
        return entry is null ? null : ToResponse(entry);
    }

    private static void Validate(PostTransactionRequest request)
    {
        if (request.AmountInCents <= 0) throw new FinancialApplicationException("AmountInCents deve ser maior que zero.", 400);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
            throw new FinancialApplicationException("IdempotencyKey deve ter entre 1 e 100 caracteres.", 400);
        if (request.Type is not ("credit" or "debit")) throw new FinancialApplicationException("Type deve ser 'credit' ou 'debit'.", 400);
        if (request.Description?.Length > 250) throw new FinancialApplicationException("Description deve ter no máximo 250 caracteres.", 400);
    }

    private static TransactionType ParseType(string type) => type == "credit" ? TransactionType.Credit : TransactionType.Debit;

    private DateTimeOffset NextOccurrenceAt(DateTimeOffset lastRecordedAt)
    {
        var now = NormalizeToDatabasePrecision(timeProvider.GetUtcNow());
        if (now > lastRecordedAt) return now;
        try { return lastRecordedAt.AddTicks(10); }
        catch (ArgumentOutOfRangeException)
        {
            throw new FinancialApplicationException("Não há um horário representável após o último lançamento.", 422);
        }
    }

    private static DateTimeOffset NormalizeToDatabasePrecision(DateTimeOffset instant)
    {
        var utcTicks = instant.UtcDateTime.Ticks;
        return new DateTimeOffset(utcTicks - utcTicks % 10, TimeSpan.Zero);
    }

    private static void EnsureSameRequest(FinancialEntry entry, TransactionType type, Money amount, string? description)
    {
        if (entry.Type != type || entry.Amount != amount || entry.Description != description)
            throw new FinancialApplicationException("A chave de idempotência já foi usada com outro conteúdo.", 409);
    }

    private static TransactionResponse ToResponse(FinancialEntry entry) =>
        new(entry.Id, entry.AccountId, entry.Type == TransactionType.Credit ? "credit" : "debit",
            entry.Amount.Cents, entry.Description, entry.OccurredAt);
}
