using System.Globalization;
using System.Security.Claims;
using Financial.Application;

namespace Financial.Api;

public static class FinancialEndpoints
{
    public static IEndpointRouteBuilder MapFinancialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/accounts", async (ClaimsPrincipal user, FinancialApplicationService service, CancellationToken ct) =>
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
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        endpoints.MapGet("/accounts/{accountId:guid}", async (Guid accountId, ClaimsPrincipal user, FinancialApplicationService service, CancellationToken ct) =>
        {
            var ownerReference = user.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(ownerReference)) return Results.Forbid();
            var account = await service.GetAccountAsync(accountId, ownerReference, ct);
            return account is null ? NotFoundProblem() : Results.Ok(account);
        })
            .WithName("GetAccount")
            .WithSummary("Consulta uma conta")
            .WithDescription("Retorna a conta somente para o titular autenticado.")
            .Produces<AccountResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        endpoints.MapPost("/accounts/{accountId:guid}/transactions", async (Guid accountId, ClaimsPrincipal user, PostTransactionRequest request, FinancialApplicationService service, CancellationToken ct) =>
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
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        endpoints.MapGet("/accounts/{accountId:guid}/transactions/{transactionId:guid}", async (Guid accountId, Guid transactionId, ClaimsPrincipal user, FinancialApplicationService service, CancellationToken ct) =>
        {
            var ownerReference = user.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(ownerReference)) return Results.Forbid();
            var transaction = await service.GetTransactionAsync(accountId, transactionId, ownerReference, ct);
            return transaction is null ? NotFoundProblem() : Results.Ok(transaction);
        })
            .WithName("GetTransaction")
            .WithSummary("Consulta um lançamento")
            .WithDescription("Retorna um lançamento da conta somente para o titular autenticado.")
            .Produces<TransactionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        endpoints.MapGet("/accounts/{accountId:guid}/balance", async (Guid accountId, ClaimsPrincipal user, string? at, FinancialApplicationService service, CancellationToken ct) =>
        {
            var ownerReference = user.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(ownerReference)) return Results.Forbid();
            if (!TryParseInstant(at, out var instant)) return InvalidInstantProblem();

            var balance = await service.GetBalanceAsync(accountId, ownerReference, instant, ct);
            return balance is null ? NotFoundProblem() : Results.Ok(balance);
        })
            .WithName("GetBalance")
            .WithSummary("Consulta o saldo da conta")
            .WithDescription("Sem o parâmetro at, retorna o saldo atual. Com at em ISO-8601, calcula o saldo até aquele instante.")
            .Produces<BalanceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        endpoints.MapGet("/customers/me/balance", async (ClaimsPrincipal user, string? at, FinancialApplicationService service, CancellationToken ct) =>
        {
            var ownerReference = user.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(ownerReference)) return Results.Forbid();
            if (!TryParseInstant(at, out var instant)) return InvalidInstantProblem();

            var balance = await service.GetCustomerBalanceAsync(ownerReference, instant, ct);
            return balance is null ? NotFoundProblem() : Results.Ok(balance);
        })
            .WithName("GetCustomerBalance")
            .WithSummary("Consulta o saldo consolidado do titular autenticado")
            .WithDescription("Soma os saldos de todas as contas do titular. O parâmetro at é opcional e deve conter fuso horário explícito.")
            .Produces<CustomerBalanceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        return endpoints;
    }

    private static bool TryParseInstant(string? value, out DateTimeOffset? instant)
    {
        instant = null;
        if (value is null) return true;
        string[] formats =
        [
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
        ];

        if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;

        instant = parsed;
        return true;
    }

    private static IResult InvalidInstantProblem() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["at"] = ["Informe uma data ISO-8601 válida com fuso horário explícito, como Z ou ±HH:mm."] });

    private static IResult NotFoundProblem() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Recurso não encontrado.");

}
