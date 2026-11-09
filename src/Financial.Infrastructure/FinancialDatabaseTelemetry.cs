using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Financial.Infrastructure;

public static class FinancialDatabaseTelemetry
{
    public const string InstrumentationName = "Financial.Infrastructure";
    public static readonly ActivitySource ActivitySource = new(InstrumentationName);
    private static readonly Meter Meter = new(InstrumentationName, "1.0.0");

    public static readonly Histogram<double> AccountLockDuration =
        Meter.CreateHistogram<double>("financial.database.account_lock.duration", "ms");
    public static readonly Histogram<double> WriteBatchDuration =
        Meter.CreateHistogram<double>("financial.database.write_batch.duration", "ms");
    public static readonly Histogram<double> CommitDuration =
        Meter.CreateHistogram<double>("financial.database.commit.duration", "ms");
    public static readonly Histogram<long> WriteBatchCommandCount =
        Meter.CreateHistogram<long>("financial.database.write_batch.commands", "{command}");
}
