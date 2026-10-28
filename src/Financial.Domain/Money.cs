namespace Financial.Domain;

public readonly record struct Money(long Cents)
{
    public static Money Zero => new(0);

    public Money Add(Money other)
    {
        try { return new Money(checked(Cents + other.Cents)); }
        catch (OverflowException) { throw new DomainRuleException("O valor excede o limite suportado."); }
    }
}

public class DomainRuleException(string message) : Exception(message);

public sealed class InsufficientFundsException() : DomainRuleException("Saldo insuficiente.");
