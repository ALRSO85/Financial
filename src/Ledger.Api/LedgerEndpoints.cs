using System.Globalization;
using System.Security.Claims;
using Ledger.Application;

namespace Ledger.Api;

public static class LedgerEndpoints
{
    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/accounts", async (ClaimsPrincipal user, LedgerApplicationService service, CancellationToken ct) =>
        {
            var ownerReference = user.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(ownerReference)) return Results.Forbid();
            var result = await service.CreateAccountAsync(ownerReference, ct);
            return Results.Created($"/accounts/{result.Id}", result);
        })
            .WithName("CreateAccount")
            .WithSummary("Cria uma conta")
            .WithDescription("Cria uma conta vinculada a uma referência opaca do titular.")
            .Produces<AccountResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        endpoints.MapPost("/accounts/{accountId:guid}/transactions", async (Guid accountId, ClaimsPrincipal user, PostTransactionRequest request, LedgerApplicationService service, CancellationToken ct) =>
        {
            var ownerReference = user.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(ownerReference)) return Results.Forbid();
            var result = await service.PostAsync(accountId, ownerReference, request, ct);
            return result.IsNew
                ? Results.Created($"/accounts/{accountId}/transactions/{result.Transaction.Id}", result.Transaction)
                : Results.Ok(result.Transaction);
        })
            .WithName("PostTransaction")
            .WithSummary("Registra um crédito ou débito")
            .WithDescription("Os valores são informados em centavos. Reenvie com a mesma chave de idempotência após timeout.")
            .Produces<TransactionResponse>(StatusCodes.Status201Created)
            .Produces<TransactionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        endpoints.MapGet("/accounts/{accountId:guid}/balance", async (Guid accountId, ClaimsPrincipal user, string? at, LedgerApplicationService service, CancellationToken ct) =>
        {
            var ownerReference = user.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(ownerReference)) return Results.Forbid();
            DateTimeOffset? instant = null;
            if (at is not null)
            {
                if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["at"] = ["Informe uma data ISO-8601 válida."] });
                instant = parsed;
            }

            var balance = await service.GetBalanceAsync(accountId, ownerReference, instant, ct);
            return balance is null ? Results.NotFound() : Results.Ok(balance);
        })
            .WithName("GetBalance")
            .WithSummary("Consulta o saldo da conta")
            .WithDescription("Sem o parâmetro at, retorna o saldo atual. Com at em ISO-8601, calcula o saldo até aquele instante.")
            .Produces<BalanceResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        return endpoints;
    }
}
