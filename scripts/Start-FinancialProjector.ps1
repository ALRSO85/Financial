$workerProject = Join-Path $PSScriptRoot "../src/Financial.Projector.Worker/Financial.Projector.Worker.csproj"
if (-not (Test-Path -LiteralPath $workerProject)) {
    throw "Não foi possível localizar o worker projetor: $workerProject"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "O .NET SDK não foi encontrado no PATH. Instale o SDK 10 antes de iniciar o projetor."
}

$variables = [ordered]@{
    DOTNET_ENVIRONMENT = "Development"
    ConnectionStrings__FinancialWrite = "Host=localhost;Port=5432;Database=financial;Username=financial;Password=financial-local-only"
    ConnectionStrings__FinancialRead = "Host=localhost;Port=5433;Database=financial_read;Username=financial;Password=financial-local-only"
}
$previousValues = @{}
foreach ($name in $variables.Keys) {
    $previousValues[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    [Environment]::SetEnvironmentVariable($name, $variables[$name], "Process")
}

try {
    & dotnet run --project $workerProject
    if ($LASTEXITCODE -ne 0) {
        throw "O worker projetor encerrou com código de saída $LASTEXITCODE."
    }
}
finally {
    foreach ($name in $variables.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousValues[$name], "Process")
    }
}
