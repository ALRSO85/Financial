using Financial.Application;
using Financial.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];
var jwtAuthority = builder.Configuration["Jwt:Authority"];
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"];

if (string.IsNullOrWhiteSpace(jwtIssuer) || string.IsNullOrWhiteSpace(jwtAudience))
    throw new InvalidOperationException("Configure Jwt:Issuer e Jwt:Audience para validar tokens.");
if (string.IsNullOrWhiteSpace(jwtSigningKey) && string.IsNullOrWhiteSpace(jwtAuthority))
    throw new InvalidOperationException("Configure Jwt:Authority ou Jwt:SigningKey.");
if (!string.IsNullOrWhiteSpace(jwtSigningKey) && !string.IsNullOrWhiteSpace(jwtAuthority))
    throw new InvalidOperationException("Configure Jwt:Authority ou Jwt:SigningKey, não ambos.");
if (!string.IsNullOrWhiteSpace(jwtSigningKey) && Encoding.UTF8.GetByteCount(jwtSigningKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey deve conter pelo menos 32 bytes.");

builder.Services.AddFinancialPersistence(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FinancialApplicationService>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<FinancialExceptionHandler>();
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
            if (context.Principal?.FindFirst("sub") is null)
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
builder.Services.AddAuthorization();

var app = builder.Build();
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
app.UseAuthentication();
app.UseAuthorization();
app.MapFinancialEndpoints();

app.Run();

public partial class Program;
