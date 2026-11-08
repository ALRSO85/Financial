param(
    [string]$SshTarget = "alexandre@192.168.88.200"
)

$ErrorActionPreference = "Stop"
$database = "financial_read_loadtest"
$container = "financial-read-db"
$sqlFile = Join-Path $PSScriptRoot "Seed-FinancialReadLoadTest.sql"

if (-not (Test-Path -LiteralPath $sqlFile)) {
    throw "Arquivo de dados de teste não encontrado: $sqlFile"
}
if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    throw "O cliente OpenSSH não foi encontrado no PATH."
}

function Invoke-Remote([string]$Command) {
    & ssh -o BatchMode=yes $SshTarget $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Comando remoto falhou no host $SshTarget."
    }
}

$exists = & ssh -o BatchMode=yes $SshTarget "docker exec $container psql -U financial -d postgres -Atc `"SELECT count(*) FROM pg_database WHERE datname='$database'`""
if ($LASTEXITCODE -ne 0) {
    throw "Não foi possível verificar o banco $database no host $SshTarget."
}
if ($exists.Trim() -eq "0") {
    $dump = "/tmp/${database}_schema.dump"
    Invoke-Remote "docker exec $container pg_dump -U financial -d financial_read --schema-only -Fc -f $dump"
    Invoke-Remote "docker exec $container createdb -U financial -T template0 $database"
    Invoke-Remote "docker exec $container pg_restore -U financial -d $database --no-owner $dump"
    Invoke-Remote "docker exec $container rm -f $dump"
}

Get-Content -LiteralPath $sqlFile -Raw | & ssh -o BatchMode=yes $SshTarget "docker exec -i $container psql -v ON_ERROR_STOP=1 -U financial -d $database"
if ($LASTEXITCODE -ne 0) {
    throw "A carga dos dados de teste falhou no banco $database."
}

Write-Output "Fixtures mantidos no banco $database do container $container em $SshTarget."
