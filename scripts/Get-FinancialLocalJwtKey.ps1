$ErrorActionPreference = "Stop"

$isWindowsPlatform = if ($PSVersionTable.PSVersion.Major -ge 6) {
    $IsWindows
} else {
    $true
}

if ($isWindowsPlatform) {
    $configRoot = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($configRoot)) {
        throw "LOCALAPPDATA não está definido; não é possível guardar a chave local com segurança."
    }
} else {
    $configRoot = $env:XDG_CONFIG_HOME
    if ([string]::IsNullOrWhiteSpace($configRoot)) {
        $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        if ([string]::IsNullOrWhiteSpace($userProfile)) {
            throw "Não foi possível localizar o diretório de configuração do usuário."
        }
        $configRoot = Join-Path $userProfile ".config"
    }
}

$secretDirectory = Join-Path $configRoot "FinancialChallenge"
$keyPath = Join-Path $secretDirectory "local-jwt-signing-key"
New-Item -ItemType Directory -Path $secretDirectory -Force | Out-Null

if ($isWindowsPlatform) {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
    & icacls $secretDirectory /inheritance:r /grant:r "*$($currentUser.Value):(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível restringir as permissões do diretório da chave."
    }
} else {
    & chmod 700 $secretDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível restringir as permissões do diretório da chave."
    }
}

if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
    $randomBytes = [byte[]]::new(32)
    $randomGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $randomGenerator.GetBytes($randomBytes)
    }
    finally {
        $randomGenerator.Dispose()
    }
    $newKey = [Convert]::ToBase64String($randomBytes)
    $keyBytes = [Text.Encoding]::ASCII.GetBytes($newKey)
    $stream = $null
    try {
        $stream = [IO.File]::Open($keyPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Write($keyBytes, 0, $keyBytes.Length)
        $stream.Flush($true)
    }
    catch [IO.IOException] {
        if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
            throw
        }
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

if ($isWindowsPlatform) {
    & icacls $keyPath /inheritance:r /grant:r "*$($currentUser.Value):F" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível restringir as permissões do arquivo de chave."
    }
} else {
    & chmod 600 $keyPath
    if ($LASTEXITCODE -ne 0) {
        throw "Não foi possível restringir as permissões do arquivo de chave."
    }
}

$key = [IO.File]::ReadAllText($keyPath, [Text.Encoding]::ASCII)
try {
    $decodedKey = [Convert]::FromBase64String($key)
} catch {
    throw "A chave JWT local armazenada está inválida; remova o arquivo protegido e gere outra."
}
if ($decodedKey.Length -ne 32) {
    throw "A chave JWT local deve conter exatamente 32 bytes; remova o arquivo protegido e gere outra."
}

Write-Output $key
