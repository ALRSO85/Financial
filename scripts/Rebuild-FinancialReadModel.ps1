[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param()

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "Docker não foi encontrado no PATH."
}

Write-Warning "Pare manualmente o Financial.Projector.Worker antes de continuar. A projeção de leitura ficará indisponível durante a reconstrução."
if (-not $PSCmdlet.ShouldProcess("banco Financial de leitura local", "Apagar projeção e reprocessar a outbox financeira")) {
    return
}

$readSql = "TRUNCATE TABLE financial_read_entries, financial_read_accounts, financial_projected_events, financial_projection_checkpoint, financial_projection_status CASCADE;"
& docker compose exec -T financial-read-db psql -v ON_ERROR_STOP=1 -U financial -d financial_read -c $readSql
if ($LASTEXITCODE -ne 0) {
    throw "Não foi possível limpar a projeção de leitura. A outbox não foi alterada."
}

$writeSql = "UPDATE financial_outbox SET processed_at = NULL, dead_lettered_at = NULL, attempts = 0, last_error = NULL;"
& docker compose exec -T financial-write-db psql -v ON_ERROR_STOP=1 -U financial -d financial -c $writeSql
if ($LASTEXITCODE -ne 0) {
    throw "A projeção foi limpa, mas a outbox não foi reiniciada. Mantenha o worker parado e execute o procedimento novamente após corrigir a conexão."
}

Write-Output "Projeção limpa e outbox reiniciada. Inicie a API read para aplicar migrations e depois inicie o projetor."
