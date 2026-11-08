using Financial.Application;
using Financial.Api;
using Financial.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Security.Claims;
using System.Threading.RateLimiting;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];
var jwtAuthority = builder.Configuration["Jwt:Authority"];
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"];
var apiRole = builder.Configuration["Financial:ApiRole"] ?? "combined";
if (apiRole is not ("write" or "read" or "combined"))
    throw new InvalidOperationException("Financial:ApiRole deve ser 'write', 'read' ou 'combined'.");
if (apiRole == "combined" && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("O modo combined é exclusivo de desenvolvimento/teste; implante os modos write e read separadamente.");
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(jwtAuthority))
    throw new InvalidOperationException("Fora de Development, Jwt:Authority deve apontar para um provedor de identidade confiável.");
if (!builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(jwtSigningKey))
    throw new InvalidOperationException("Jwt:SigningKey simétrica só é aceita em Development; use Jwt:Authority fora do ambiente local.");

if (string.IsNullOrWhiteSpace(jwtIssuer) || string.IsNullOrWhiteSpace(jwtAudience))
    throw new InvalidOperationException("Configure Jwt:Issuer e Jwt:Audience para validar tokens.");
if (string.IsNullOrWhiteSpace(jwtSigningKey) && string.IsNullOrWhiteSpace(jwtAuthority))
    throw new InvalidOperationException("Configure Jwt:Authority ou Jwt:SigningKey.");
if (!string.IsNullOrWhiteSpace(jwtSigningKey) && !string.IsNullOrWhiteSpace(jwtAuthority))
    throw new InvalidOperationException("Configure Jwt:Authority ou Jwt:SigningKey, não ambos.");
if (!string.IsNullOrWhiteSpace(jwtSigningKey) && Encoding.UTF8.GetByteCount(jwtSigningKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey deve conter pelo menos 32 bytes.");

builder.Services.AddFinancialPersistence(builder.Configuration, builder.Environment, apiRole);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
});
builder.Services.AddSingleton(TimeProvider.System);
if (apiRole is "write" or "combined")
    builder.Services.AddSingleton<FinancialApplicationService>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<FinancialExceptionHandler>();
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck("process", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"]);
if (apiRole is "write" or "read")
{
    builder.Services.AddSingleton<PostgresReadinessHealthCheck>();
    healthChecks.AddCheck<PostgresReadinessHealthCheck>("postgres", tags: ["ready"]);
}
if (apiRole == "read")
{
    builder.Services.AddSingleton<ProjectionFreshnessHealthCheck>();
    healthChecks.AddCheck<ProjectionFreshnessHealthCheck>("projection-freshness", tags: ["ready"]);
}
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Financial API", Version = "v1" });
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Informe o access token JWT emitido para este cliente."
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("bearer", document)] = []
    });
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.Authority = jwtAuthority;
    options.Audience = jwtAudience;
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub"
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (string.IsNullOrWhiteSpace(context.Principal?.FindFirst("sub")?.Value))
                context.Fail("O token não contém o claim obrigatório sub.");
            return Task.CompletedTask;
        }
    };

    if (!string.IsNullOrWhiteSpace(jwtSigningKey))
    {
        options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey));
        options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
    }
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("financial.read", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "financial.read")));
    options.AddPolicy("financial.write", policy => policy.RequireAuthenticatedUser().RequireAssertion(context => HasScope(context.User, "financial.write")));
});
var globalRateLimit = ReadPositiveInt(builder.Configuration, "RateLimits:Global:TokenLimit", 1000);
var globalTokensPerPeriod = ReadPositiveInt(builder.Configuration, "RateLimits:Global:TokensPerPeriod", 1000);
var globalPeriodSeconds = ReadPositiveInt(builder.Configuration, "RateLimits:Global:ReplenishmentPeriodSeconds", 60);
var readRateLimit = ReadPositiveInt(builder.Configuration, "RateLimits:Read:TokenLimit", 300);
var readTokensPerPeriod = ReadPositiveInt(builder.Configuration, "RateLimits:Read:TokensPerPeriod", 300);
var readPeriodSeconds = ReadPositiveInt(builder.Configuration, "RateLimits:Read:ReplenishmentPeriodSeconds", 60);
var writeRateLimit = ReadPositiveInt(builder.Configuration, "RateLimits:Write:TokenLimit", 60);
var writeTokensPerPeriod = ReadPositiveInt(builder.Configuration, "RateLimits:Write:TokensPerPeriod", 60);
var writePeriodSeconds = ReadPositiveInt(builder.Configuration, "RateLimits:Write:ReplenishmentPeriodSeconds", 60);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetTokenBucketLimiter(GetClientRateLimitKey(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = globalRateLimit,
            TokensPerPeriod = globalTokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(globalPeriodSeconds),
            AutoReplenishment = true,
            QueueLimit = 0
        }));
    options.AddPolicy("financial.read", context => RateLimitPartition.GetTokenBucketLimiter(GetRateLimitKey(context), _ =>
        new TokenBucketRateLimiterOptions
        {
            TokenLimit = readRateLimit,
            TokensPerPeriod = readTokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(readPeriodSeconds),
            AutoReplenishment = true,
            QueueLimit = 0
        }));
    options.AddPolicy("financial.write", context => RateLimitPartition.GetTokenBucketLimiter(GetRateLimitKey(context), _ =>
        new TokenBucketRateLimiterOptions
        {
            TokenLimit = writeRateLimit,
            TokensPerPeriod = writeTokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(writePeriodSeconds),
            AutoReplenishment = true,
            QueueLimit = 0
        }));
});

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService($"Financial.{apiRole}"))
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(options =>
        options.EnrichWithHttpRequest = (activity, request) =>
        {
            if (request.HttpContext.GetEndpoint() is RouteEndpoint endpoint && endpoint.RoutePattern.RawText is { } route)
            {
                activity.SetTag("http.route", route);
                activity.SetTag("url.path", route);
                activity.SetTag("http.target", route);
                activity.SetTag("url.full", $"{request.Scheme}://{request.Host}{route}");
            }
        }).AddHttpClientInstrumentation()
        .AddSource(FinancialTelemetry.InstrumentationName))
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
        .AddMeter(FinancialTelemetry.InstrumentationName, "Financial.Infrastructure"));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    telemetry.WithTracing(tracing => tracing.AddOtlpExporter())
        .WithMetrics(metrics => metrics.AddOtlpExporter())
        .WithLogging(logging => logging.AddOtlpExporter());

static string GetRateLimitKey(HttpContext context) =>
    context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static int ReadPositiveInt(IConfiguration configuration, string key, int defaultValue)
{
    var value = configuration.GetValue<int?>(key) ?? defaultValue;
    return value > 0
        ? value
        : throw new InvalidOperationException($"A configuração {key} deve ser maior que zero.");
}

static string GetClientRateLimitKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static bool HasScope(ClaimsPrincipal principal, string requiredScope) => principal.FindAll("scope").Concat(principal.FindAll("scp"))
    .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    .Contains(requiredScope, StringComparer.Ordinal);

var app = builder.Build();
if (app.Environment.IsDevelopment() && app.Services.GetService<PostgresFinancialDatabaseInitializer>() is { } databaseInitializer)
    await databaseInitializer.InitializeAsync(app.Lifetime.ApplicationStopping);
if (app.Environment.IsDevelopment() && app.Services.GetService<PostgresFinancialReadDatabaseInitializer>() is { } readDatabaseInitializer)
    await readDatabaseInitializer.InitializeAsync(app.Lifetime.ApplicationStopping);
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseStaticFiles();
    app.MapGet("/", () => Results.Content("""
        <!doctype html>
        <html lang="pt-BR">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <link rel="icon" href="/favicon.ico" sizes="any">
            <title>Financial API</title>
            <style>
                body { margin: 0; background: #f4f6f8; color: #17202a; font: 16px system-ui, sans-serif; }
                main { box-sizing: border-box; max-width: 680px; margin: 12vh auto; padding: 40px; background: #fff; border-radius: 12px; box-shadow: 0 8px 30px #17202a12; }
                h1 { margin-top: 0; }
                a { display: inline-block; margin-top: 12px; padding: 12px 18px; border-radius: 6px; background: #155eef; color: #fff; text-decoration: none; }
                a:hover { background: #124fc7; }
                code { color: #344054; }
            </style>
        </head>
        <body>
            <main>
                <h1>Financial API</h1>
                <p>API demonstrativa para registrar créditos e débitos e consultar saldos atuais ou históricos.</p>
                <a href="/swagger">Abrir documentação Swagger</a>
                <p>Documento OpenAPI: <code>/swagger/v1/swagger.json</code></p>
            </main>
        </body>
        </html>
        """, "text/html; charset=utf-8")).ExcludeFromDescription();
}
app.UseExceptionHandler();
app.UseRouting();
app.Use(async (context, next) =>
{
    var correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    var route = context.GetEndpoint() is RouteEndpoint routeEndpoint
        ? routeEndpoint.RoutePattern.RawText ?? "unmatched"
        : "unmatched";
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        return Task.CompletedTask;
    });
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
        app.Logger.LogInformation("HTTP request completed {HttpMethod} {Route} {StatusCode}",
            context.Request.Method, route, context.Response.StatusCode);
    }
});
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes = { [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK }
});
if (apiRole == "write") app.MapWriteFinancialEndpoints();
else if (apiRole == "read") app.MapReadFinancialEndpoints();
else app.MapFinancialEndpoints();

app.Run();

public partial class Program;
