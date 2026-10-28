param(
    [string]$Subject = "customer-123"
)

$issuer = $env:Jwt__Issuer
$audience = $env:Jwt__Audience
$signingKey = $env:Jwt__SigningKey

if ([string]::IsNullOrWhiteSpace($issuer) -or [string]::IsNullOrWhiteSpace($audience)) {
    throw "Defina Jwt__Issuer e Jwt__Audience antes de gerar um token local."
}
if ([string]::IsNullOrWhiteSpace($signingKey) -or [Text.Encoding]::UTF8.GetByteCount($signingKey) -lt 32) {
    throw "Defina Jwt__SigningKey com pelo menos 32 bytes."
}

function ConvertTo-Base64Url([byte[]]$Bytes) {
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"HS256","typ":"JWT"}'))
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$payloadObject = @{
    iss = $issuer
    aud = $audience
    sub = $Subject
    iat = $now
    nbf = $now
    exp = $now + 300
}
$payloadJson = ConvertTo-Json -InputObject $payloadObject -Compress
$payload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($payloadJson))
$unsignedToken = "$header.$payload"
$hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($signingKey))
try {
    $signature = ConvertTo-Base64Url ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($unsignedToken)))
}
finally {
    $hmac.Dispose()
}

Write-Output "$unsignedToken.$signature"
