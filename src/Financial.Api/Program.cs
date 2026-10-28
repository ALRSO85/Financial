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
}
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapFinancialEndpoints();

app.Run();

public partial class Program;
