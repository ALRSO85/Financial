using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

var options = ParseOptions(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: Financial.LoadTest --allow-write-load --base-url URL --account-ids GUID,GUID,... --duration 00:01:00 --concurrency 150 [--requests-per-second 500]");
    Console.Error.WriteLine("Set FINANCIAL_LOADTEST_TOKEN in the environment. This tool writes synthetic 1-cent credits to the supplied account.");
    return 2;
}

var token = Environment.GetEnvironmentVariable("FINANCIAL_LOADTEST_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("FINANCIAL_LOADTEST_TOKEN is required.");
    return 2;
}

using var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = options.Concurrency,
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    AutomaticDecompression = DecompressionMethods.None
};
using var client = new HttpClient(handler) { BaseAddress = options.BaseUrl, Timeout = TimeSpan.FromSeconds(30) };
client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

var runId = Guid.NewGuid().ToString("N");
var latencies = new ConcurrentBag<double>();
var statuses = new ConcurrentDictionary<int, long>();
var failures = new ConcurrentBag<string>();
long sequence = 0;
long scheduledSlot = 0;
var stopwatch = Stopwatch.StartNew();

var workers = Enumerable.Range(0, options.Concurrency).Select(_ => Task.Run(async () =>
{
    while (stopwatch.Elapsed < options.Duration)
    {
        var slot = Interlocked.Increment(ref scheduledSlot);
        if (options.RequestsPerSecond is { } rate)
        {
            var dueAt = TimeSpan.FromSeconds((slot - 1) / (double)rate);
            var delay = dueAt - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            if (stopwatch.Elapsed >= options.Duration) break;
        }
        var requestId = Interlocked.Increment(ref sequence);
        var payload = new
        {
            type = "credit",
            amountInCents = 1,
            idempotencyKey = $"load-{runId}-{requestId}",
            description = "synthetic load test"
        };
        var accountId = options.AccountIds[(int)((requestId - 1) % options.AccountIds.Length)];
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await client.PostAsJsonAsync(
                $"/accounts/{accountId:D}/transactions", payload);
            await response.Content.LoadIntoBufferAsync();
            timer.Stop();
            latencies.Add(timer.Elapsed.TotalMilliseconds);
            statuses.AddOrUpdate((int)response.StatusCode, 1, (_, count) => count + 1);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            timer.Stop();
            latencies.Add(timer.Elapsed.TotalMilliseconds);
            failures.Add(exception.GetType().Name);
        }
    }
})).ToArray();

await Task.WhenAll(workers);
stopwatch.Stop();

var sorted = latencies.Order().ToArray();
var accepted = statuses.GetValueOrDefault((int)HttpStatusCode.Created) + statuses.GetValueOrDefault((int)HttpStatusCode.OK);
var report = new
{
    startedAtUtc = DateTimeOffset.UtcNow - stopwatch.Elapsed,
    durationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
    concurrency = options.Concurrency,
    requestedRatePerSecond = options.RequestsPerSecond,
    requests = sequence,
    accepted,
    requestsPerSecond = Math.Round(accepted / stopwatch.Elapsed.TotalSeconds, 2),
    httpStatusCounts = statuses.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
    transportFailures = failures.Count,
    latencyMs = new
    {
        p50 = Percentile(sorted, 0.50),
        p95 = Percentile(sorted, 0.95),
        p99 = Percentile(sorted, 0.99),
        max = sorted.Length == 0 ? 0 : Math.Round(sorted[^1], 2)
    }
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return accepted == sequence && failures.IsEmpty ? 0 : 1;

static double Percentile(double[] sorted, double percentile)
{
    if (sorted.Length == 0) return 0;
    var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
    return Math.Round(sorted[index], 2);
}

static LoadOptions? ParseOptions(string[] arguments)
{
    if (!arguments.Contains("--allow-write-load", StringComparer.Ordinal)) return null;
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index++)
    {
        var key = arguments[index];
        if (key == "--allow-write-load") continue;
        if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length) return null;
        values[key] = arguments[++index];
    }

    if (!values.TryGetValue("--base-url", out var baseUrlText) ||
        !Uri.TryCreate(baseUrlText, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("http" or "https") ||
        !values.TryGetValue("--account-ids", out var accountsText) ||
        accountsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is not { Length: > 0 and <= 1000 } accountParts ||
        accountParts.Any(account => !Guid.TryParse(account, out _)) ||
        !values.TryGetValue("--duration", out var durationText) || !TimeSpan.TryParse(durationText, out var duration) ||
        duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(10) ||
        !values.TryGetValue("--concurrency", out var concurrencyText) || !int.TryParse(concurrencyText, out var concurrency) ||
        concurrency < 1 || concurrency > 500)
        return null;

    int? requestsPerSecond = null;
    if (values.TryGetValue("--requests-per-second", out var rateText))
    {
        if (!int.TryParse(rateText, out var rate) || rate < 1 || rate > 5000) return null;
        requestsPerSecond = rate;
    }

    var accountIds = accountsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Guid.Parse).Distinct().ToArray();
    if (accountIds.Length != accountsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length)
        return null;
    return new LoadOptions(baseUrl, accountIds, duration, concurrency, requestsPerSecond);
}

internal sealed record LoadOptions(Uri BaseUrl, Guid[] AccountIds, TimeSpan Duration, int Concurrency,
    int? RequestsPerSecond);
