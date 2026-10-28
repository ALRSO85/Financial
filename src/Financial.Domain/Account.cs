namespace Financial.Domain;

public sealed record Account(Guid Id, string OwnerReference, DateTimeOffset CreatedAt, Money Balance)
{
    public static Account Open(string ownerReference, DateTimeOffset createdAt) =>
        new(Guid.NewGuid(), ownerReference, createdAt, Money.Zero);

    public Account Apply(TransactionType type, Money amount)
    {
        if (type is not (TransactionType.Credit or TransactionType.Debit))
            throw new DomainRuleException("Tipo de movimentação inválido.");
        if (amount.Cents <= 0) throw new DomainRuleException("O valor da movimentação deve ser maior que zero.");

        var delta = type == TransactionType.Credit ? amount : new Money(-amount.Cents);
        var updatedBalance = Balance.Add(delta);
        if (updatedBalance.Cents < 0) throw new InsufficientFundsException();

        return this with { Balance = updatedBalance };
    }
}

public enum TransactionType
{
    Credit,
    Debit
}
