using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Financial.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Financial.Api.Tests;

public sealed class AuthenticationTests : IAsyncLifetime
{
    private const string Issuer = "financial-test-issuer";
    private const string Audience = "financial-test-api";
    private const string SigningKey = "test-only-signing-key-with-at-least-32-bytes";
    private readonly Dictionary<string, string?> _previousEnvironment = new();
    private string _filePath = null!;
    private WebApplicationFactory<global::Program> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "financial-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "financial.json");
        SetEnvironmentVariable("Jwt__Issuer", Issuer);
        SetEnvironmentVariable("Jwt__Audience", Audience);
        SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
        SetEnvironmentVariable("Financial__FilePath", _filePath);
        _factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        foreach (var (name, value) in _previousEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        var directory = Path.GetDirectoryName(_filePath);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private void SetEnvironmentVariable(string name, string value)
    {
        _previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public async Task Requests_require_valid_bearer_token_and_account_owner()
    {
        using var openApi = JsonDocument.Parse(await _client.GetStringAsync("/swagger/v1/swagger.json"));
        var openApiRoot = openApi.RootElement;
        Assert.True(openApiRoot.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearer", out _));
        Assert.Contains(openApiRoot.GetProperty("security").EnumerateArray(), requirement =>
            requirement.TryGetProperty("bearer", out _));
        var paths = openApiRoot.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/accounts/{accountId}", out _));
        Assert.True(paths.TryGetProperty("/accounts/{accountId}/transactions/{transactionId}", out _));
        Assert.True(paths.TryGetProperty("/customers/me/balance", out _));
        var insufficientFundsResponse = paths.GetProperty("/accounts/{accountId}/transactions")
            .GetProperty("post").GetProperty("responses").GetProperty("422");
        Assert.True(insufficientFundsResponse.GetProperty("content").TryGetProperty("application/problem+json", out _));

        var anonymousResponse = await _client.PostAsync("/accounts", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        var invalidTokenResponse = await _client.PostAsync("/accounts", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidTokenResponse.StatusCode);

        foreach (var invalidToken in new[]
        {
            CreateToken("cliente-123", issuer: "untrusted-issuer"),
            CreateToken("cliente-123", audience: "another-api"),
            CreateToken("cliente-123", signingKey: "different-test-signing-key-with-32-bytes"),
            CreateToken("cliente-123", expires: DateTime.UtcNow.AddMinutes(-5)),
            CreateToken(subject: null)
        })
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);
            var response = await _client.PostAsync("/accounts", content: null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("customer-123"));
        var createResponse = await _client.PostAsync("/accounts", content: null);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var account = (await JsonSerializer.DeserializeAsync<AccountResponse>(await createResponse.Content.ReadAsStreamAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        var accountLocation = createResponse.Headers.Location;
        Assert.NotNull(accountLocation);
        var accountResource = await _client.GetAsync(accountLocation);
        Assert.Equal(HttpStatusCode.OK, accountResource.StatusCode);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("customer-456"));
        var otherOwnerAccount = await _client.GetAsync(accountLocation);
        Assert.Equal(HttpStatusCode.NotFound, otherOwnerAccount.StatusCode);
        var otherOwnerResponse = await _client.GetAsync($"/accounts/{account.Id}/balance");
        Assert.Equal(HttpStatusCode.NotFound, otherOwnerResponse.StatusCode);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("customer-123"));
        var ownerResponse = await _client.GetAsync($"/accounts/{account.Id}/balance");
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        var ambiguousInstant = await _client.GetAsync($"/accounts/{account.Id}/balance?at=28%2F10%2F2026%2009%3A00%3A00");
        Assert.Equal(HttpStatusCode.BadRequest, ambiguousInstant.StatusCode);
        Assert.Equal("application/problem+json", ambiguousInstant.Content.Headers.ContentType?.MediaType);
        var localizedInstant = await _client.GetAsync($"/accounts/{account.Id}/balance?at=28%2F10%2F2026%2009%3A00%3A00%20-03%3A00");
        Assert.Equal(HttpStatusCode.OK, localizedInstant.StatusCode);
        var explicitInstant = await _client.GetAsync($"/accounts/{account.Id}/balance?at=28%2F10%2F2026%2012%3A00%3A00%20%2B00%3A00");
        Assert.Equal(HttpStatusCode.OK, explicitInstant.StatusCode);
        var isoInstant = await _client.GetAsync($"/accounts/{account.Id}/balance?at=2026-10-28T12:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, isoInstant.StatusCode);
        var utcBalance = (await JsonSerializer.DeserializeAsync<BalanceResponse>(
            await explicitInstant.Content.ReadAsStreamAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        var brazilianBalance = (await JsonSerializer.DeserializeAsync<BalanceResponse>(
            await localizedInstant.Content.ReadAsStreamAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.Equal(utcBalance.AsOf, brazilianBalance.AsOf);
        Assert.Equal(utcBalance.BalanceInCents, brazilianBalance.BalanceInCents);

        var transactionResponse = await _client.PostAsJsonAsync($"/accounts/{account.Id}/transactions",
            new PostTransactionRequest("credit", 1250, "api-credit-1", "depósito"));
        Assert.Equal(HttpStatusCode.Created, transactionResponse.StatusCode);
        var transaction = (await JsonSerializer.DeserializeAsync<TransactionResponse>(
            await transactionResponse.Content.ReadAsStreamAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        var createdLocation = transactionResponse.Headers.Location;
        Assert.NotNull(createdLocation);

        var transactionResource = await _client.GetAsync(createdLocation);
        Assert.Equal(HttpStatusCode.OK, transactionResource.StatusCode);
        var storedTransaction = (await JsonSerializer.DeserializeAsync<TransactionResponse>(
            await transactionResource.Content.ReadAsStreamAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.Equal(transaction.Id, storedTransaction.Id);

        var customerBalanceResponse = await _client.GetAsync("/customers/me/balance");
        Assert.Equal(HttpStatusCode.OK, customerBalanceResponse.StatusCode);
        var customerBalance = (await JsonSerializer.DeserializeAsync<CustomerBalanceResponse>(
            await customerBalanceResponse.Content.ReadAsStreamAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.Equal(1250, customerBalance.BalanceInCents);
        Assert.Equal(1, customerBalance.AccountCount);

        var insufficientDebit = await _client.PostAsJsonAsync($"/accounts/{account.Id}/transactions",
            new PostTransactionRequest("debit", 1251, "api-insufficient-1", "compra"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, insufficientDebit.StatusCode);
        Assert.Equal("application/problem+json", insufficientDebit.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await insufficientDebit.Content.ReadAsStringAsync());
        Assert.Equal(422, problem.RootElement.GetProperty("status").GetInt32());

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("customer-456"));
        var otherOwnerTransaction = await _client.GetAsync(createdLocation);
        Assert.Equal(HttpStatusCode.NotFound, otherOwnerTransaction.StatusCode);
        Assert.Equal("application/problem+json", otherOwnerTransaction.Content.Headers.ContentType?.MediaType);
        var otherCustomerBalance = await _client.GetAsync("/customers/me/balance");
        Assert.Equal(HttpStatusCode.NotFound, otherCustomerBalance.StatusCode);
    }

    private static string CreateToken(string? subject, string issuer = Issuer, string audience = Audience,
        DateTime? expires = null, string signingKey = SigningKey)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = subject is null ? [] : new[] { new Claim("sub", subject) };
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(5);
        var notBefore = expires is null ? DateTime.UtcNow.AddMinutes(-1) : expiry.AddMinutes(-10);
        var token = new JwtSecurityToken(issuer, audience, claims, notBefore, expiry, credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
