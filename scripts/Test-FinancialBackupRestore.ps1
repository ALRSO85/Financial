param(
    [string]$SshTarget = "alexandre@192.168.88.200",
    [string]$ContainerName = "financial-write-db",
    [string]$SourceDatabase = "financial_write_instrumented",
    [string]$RemoteBackupDirectory = "/home/alexandre/financial-backups"
)

$ErrorActionPreference = "Stop"

if ($SshTarget -notmatch '^[A-Za-z0-9_.@-]+$') {
    throw "SshTarget deve conter somente usuário e host, sem opções ou comandos."
}
if ($ContainerName -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$') {
    throw "ContainerName contém caracteres não permitidos."
}
if ($SourceDatabase -notmatch '^[A-Za-z0-9_]+$') {
    throw "SourceDatabase contém caracteres não permitidos."
}
if ($RemoteBackupDirectory -notmatch '^/[A-Za-z0-9_./-]+$' -or $RemoteBackupDirectory -match '(^|/)\.\.(/|$)') {
    throw "RemoteBackupDirectory deve ser um caminho absoluto simples, sem segmentos '..'."
}
if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    throw "O cliente OpenSSH não foi encontrado no PATH."
}

function Invoke-SshCommand([string]$RemoteCommand) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "ssh"
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add("-o")
    $startInfo.ArgumentList.Add("BatchMode=yes")
    $startInfo.ArgumentList.Add("-o")
    $startInfo.ArgumentList.Add("ConnectTimeout=10")
    $startInfo.ArgumentList.Add($SshTarget)
    $startInfo.ArgumentList.Add($RemoteCommand)

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "Comando remoto falhou (código $($process.ExitCode)): $($stderr.Trim())"
    }
    return $stdout.Trim()
}

$runId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
$timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd_HHmmss")
$backupFileName = "financial_${SourceDatabase}_${timestamp}_${runId}.dump"
$backupPath = "$RemoteBackupDirectory/$backupFileName"
$restoreDatabase = "financial_restore_validation_${timestamp}_${runId}"
$containerArchivePath = "/tmp/$backupFileName"

$snapshotSql = @"
SELECT
    (SELECT count(*) FROM financial_accounts),
    (SELECT COALESCE(sum(balance_cents), 0) FROM financial_accounts),
    (SELECT count(*) FROM financial_entries),
    (SELECT COALESCE(sum(signed_amount_cents), 0) FROM financial_entries),
    (SELECT count(*) FROM financial_outbox),
    (SELECT count(*) FROM financial_outbox WHERE processed_at IS NULL),
    (SELECT count(*) FROM financial_audit_events),
    (SELECT count(*) FROM financial_entries e LEFT JOIN financial_accounts a ON a.id = e.account_id WHERE a.id IS NULL);
"@ -replace '\s+', ' '

$prepareBackup = "umask 077 && mkdir -p '$RemoteBackupDirectory' && chmod 700 '$RemoteBackupDirectory' && docker exec '$ContainerName' pg_dump -U financial --format=custom --dbname='$SourceDatabase' > '$backupPath' && chmod 600 '$backupPath'"
[void](Invoke-SshCommand $prepareBackup)

$checksumLine = Invoke-SshCommand "sha256sum '$backupPath'"
$checksum = ($checksumLine -split '\s+')[0]
if ($checksum -notmatch '^[a-fA-F0-9]{64}$') {
    throw "O arquivo de backup não retornou um SHA-256 válido."
}
$backupSize = [long](Invoke-SshCommand "stat -c '%s' '$backupPath'")
if ($backupSize -le 0) {
    throw "O arquivo de backup está vazio."
}

[void](Invoke-SshCommand "docker cp '$backupPath' '${ContainerName}:$containerArchivePath'")
$archiveListing = Invoke-SshCommand "docker exec '$ContainerName' pg_restore --list '$containerArchivePath'"
$archiveItems = @($archiveListing -split "`r?`n" | Where-Object { $_ -match '^\s*\d+;' }).Count
if ($archiveItems -lt 1) {
    throw "O arquivo não contém uma lista de objetos restauráveis."
}

[void](Invoke-SshCommand "docker exec '$ContainerName' createdb -U financial '$restoreDatabase'")
[void](Invoke-SshCommand "docker exec '$ContainerName' pg_restore --exit-on-error --no-owner -U financial --dbname='$restoreDatabase' '$containerArchivePath'")

$sourceSnapshotCommand = "docker exec '$ContainerName' psql -U financial -d '$SourceDatabase' -At -F '|' -c `"$snapshotSql`""
$restoreSnapshotCommand = "docker exec '$ContainerName' psql -U financial -d '$restoreDatabase' -At -F '|' -c `"$snapshotSql`""
$sourceSnapshot = Invoke-SshCommand $sourceSnapshotCommand
$restoreSnapshot = Invoke-SshCommand $restoreSnapshotCommand
if ($sourceSnapshot -ne $restoreSnapshot) {
    throw "A reconciliação divergiu. Origem='$sourceSnapshot'; restauração='$restoreSnapshot'. O banco restaurado foi mantido para inspeção: $restoreDatabase"
}

$columns = $sourceSnapshot -split '\|'
if ($columns.Count -ne 8 -or [long]$columns[7] -ne 0 -or [long]$columns[1] -ne [long]$columns[3]) {
    throw "A validação do formato, referências órfãs ou reconciliação entre saldos e lançamentos falhou. Banco restaurado mantido: $restoreDatabase"
}

[pscustomobject]@{
    sourceDatabase = $SourceDatabase
    restoredDatabase = $restoreDatabase
    backupFile = $backupPath
    backupBytes = $backupSize
    sha256 = $checksum
    archiveObjects = $archiveItems
    accountCount = [long]$columns[0]
    accountBalanceCents = [long]$columns[1]
    entryCount = [long]$columns[2]
    entrySignedCents = [long]$columns[3]
    outboxCount = [long]$columns[4]
    pendingOutboxCount = [long]$columns[5]
    auditCount = [long]$columns[6]
    orphanEntryCount = [long]$columns[7]
    sourceSnapshot = $sourceSnapshot
    restoreSnapshot = $restoreSnapshot
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
} | ConvertTo-Json -Depth 3
