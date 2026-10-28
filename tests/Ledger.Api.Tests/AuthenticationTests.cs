using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Ledger.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Ledger.Api.Tests;

public sealed class AuthenticationTests : IAsyncLifetime
{
    private const string Issuer = "ledger-test-issuer";
    private const string Audience = "ledger-test-api";
    private const string SigningKey = "test-only-signing-key-with-at-least-32-bytes";
    private readonly Dictionary<string, string?> _previousEnvironment = new();
    private string _filePath = null!;
    private WebApplicationFactory<global::Program> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ledger-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "ledger.json");
        SetEnvironmentVariable("Jwt__Issuer", Issuer);
        SetEnvironmentVariable("Jwt__Audience", Audience);
        SetEnvironmentVariable("Jwt__SigningKey", SigningKey);
        SetEnvironmentVariable("Ledger__FilePath", _filePath);
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
        Assert.True(openApi.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearer", out _));

        var anonymousResponse = await _client.PostAsync("/accounts", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        var invalidTokenResponse = await _client.PostAsync("/accounts", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidTokenResponse.StatusCode);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("customer-123"));
        var createResponse = await _client.PostAsync("/accounts", content: null);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var account = (await JsonSerializer.DeserializeAsync<AccountResponse>(await createResponse.Content.ReadAsStreamAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("customer-456"));
        var otherOwnerResponse = await _client.GetAsync($"/accounts/{account.Id}/balance");
        Assert.Equal(HttpStatusCode.NotFound, otherOwnerResponse.StatusCode);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("customer-123"));
        var ownerResponse = await _client.GetAsync($"/accounts/{account.Id}/balance");
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
    }

    private static string CreateToken(string subject)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Audience, [new Claim("sub", subject)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
