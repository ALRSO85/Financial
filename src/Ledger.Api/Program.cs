using Ledger.Application;
using Ledger.Api;
using Ledger.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var ledgerFilePath = builder.Configuration["Ledger:FilePath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "ledger.json");
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];
var jwtAuthority = builder.Configuration["Jwt:Authority"];
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"];

if (string.IsNullOrWhiteSpace(jwtIssuer) || string.IsNullOrWhiteSpace(jwtAudience))
    throw new InvalidOperationException("Configure Jwt:Issuer e Jwt:Audience para validar tokens.");
if (string.IsNullOrWhiteSpace(jwtSigningKey) && string.IsNullOrWhiteSpace(jwtAuthority))
    throw new InvalidOperationException("Configure Jwt:Authority ou Jwt:SigningKey.");
if (!string.IsNullOrWhiteSpace(jwtSigningKey) && Encoding.UTF8.GetByteCount(jwtSigningKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey deve conter pelo menos 32 bytes.");

builder.Services.AddSingleton<ILedgerRepository>(_ => new JsonLedgerRepository(ledgerFilePath));
builder.Services.AddSingleton<LedgerApplicationService>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Ledger API", Version = "v1" });
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

    if (!string.IsNullOrWhiteSpace(jwtSigningKey))
        options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey));
});
builder.Services.AddAuthorization();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (LedgerApplicationException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.MapLedgerEndpoints();

app.Run();

public partial class Program;
