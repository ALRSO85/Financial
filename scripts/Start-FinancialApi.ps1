$apiProject = Join-Path $PSScriptRoot "..\src\Financial.Api\Financial.Api.csproj"
if (-not (Test-Path -LiteralPath $apiProject)) {
    throw "Não foi possível localizar o projeto da API: $apiProject"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "O .NET SDK não foi encontrado no PATH. Instale o SDK 10 antes de iniciar a API."
}
if (-not [string]::IsNullOrWhiteSpace($env:Jwt__Authority)) {
    throw "Este script é para desenvolvimento local com chave simétrica. Remova Jwt:Authority ou inicie a API com sua configuração de provedor de identidade."
}

$variables = [ordered]@{
    Jwt__Issuer = "financial-local"
    Jwt__Audience = "financial-api"
    Jwt__SigningKey = "local-only-change-this-key-to-a-random-32-byte-value"
}
$previousValues = @{}

foreach ($name in $variables.Keys) {
    $previousValues[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    [Environment]::SetEnvironmentVariable($name, $variables[$name], "Process")
}

try {
    & dotnet run --project $apiProject --launch-profile "Financial.Api"
    if ($LASTEXITCODE -ne 0) {
        throw "A API encerrou com código de saída $LASTEXITCODE."
    }
}
finally {
    foreach ($name in $variables.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousValues[$name], "Process")
    }
}
