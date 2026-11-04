using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Financial.Application;

public static class FinancialTelemetry
{
    public const string InstrumentationName = "Financial.Application";
    public static readonly ActivitySource ActivitySource = new(InstrumentationName);
    public static readonly Meter Meter = new(InstrumentationName, "1.0.0");
    public static readonly Counter<long> TransactionsAccepted = Meter.CreateCounter<long>("financial.transactions.accepted");
    public static readonly Counter<long> IdempotentReplays = Meter.CreateCounter<long>("financial.transactions.idempotent_replays");
}
