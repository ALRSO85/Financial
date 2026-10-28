using System.Globalization;
using Ledger.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<LedgerService>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (LedgerException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.MapPost("/accounts", async (CreateAccountRequest request, LedgerService service, CancellationToken ct) =>
{
    var result = await service.CreateAccountAsync(request.OwnerReference, ct);
    return Results.Created($"/accounts/{result.Id}", result);
});

app.MapPost("/accounts/{accountId:guid}/transactions", async (Guid accountId, PostTransactionRequest request, LedgerService service, CancellationToken ct) =>
{
    var result = await service.PostAsync(accountId, request, ct);
    return result.IsNew ? Results.Created($"/accounts/{accountId}/transactions/{result.Transaction.Id}", result.Transaction) : Results.Ok(result.Transaction);
});

app.MapGet("/accounts/{accountId:guid}/balance", async (Guid accountId, string? at, LedgerService service, CancellationToken ct) =>
{
    DateTimeOffset? instant = null;
    if (at is not null)
    {
        if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["at"] = ["Informe uma data ISO-8601 válida."] });
        instant = parsed;
    }

    var balance = await service.GetBalanceAsync(accountId, instant, ct);
    return balance is null ? Results.NotFound() : Results.Ok(balance);
});

app.Run();

public partial class Program;

public sealed record CreateAccountRequest(string OwnerReference);
public sealed record PostTransactionRequest(string Type, long AmountInCents, string IdempotencyKey, string? Description);
