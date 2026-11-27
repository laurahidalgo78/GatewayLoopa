<#
.SYNOPSIS
  Calcula el código TOTP (RFC 6238) de un usuario de desarrollo, leyendo su secreto de appsettings.Development.json.
  SOLO DESARROLLO: en producción el MFA lo gestiona el IdP y los secretos nunca están en archivos.
.EXAMPLE
  ./tools/Get-Totp.ps1 -User maker@tenant-a
#>
param(
    [Parameter(Mandatory = $true)][string]$User,
    [string]$Settings,
    [int]$StepOffset = 0
)

function ConvertFrom-Base32([string]$s) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $bits = 0; $buffer = 0; $out = New-Object System.Collections.Generic.List[byte]
    foreach ($c in $s.TrimEnd('=').ToUpperInvariant().ToCharArray()) {
        $buffer = (($buffer -shl 5) -bor $alphabet.IndexOf($c)) -band 0xFFFF; $bits += 5
        if ($bits -ge 8) { $out.Add([byte](($buffer -shr ($bits - 8)) -band 0xFF)); $bits -= 8 }
    }
    return ,$out.ToArray()
}

function Get-TotpCode([byte[]]$Key, [long]$Step) {
    $counter = [BitConverter]::GetBytes([long]$Step)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($counter) }
    $hmac = New-Object System.Security.Cryptography.HMACSHA1 (, $Key)
    $hash = $hmac.ComputeHash($counter)
    $offset = $hash[$hash.Length - 1] -band 0x0F
    $binary = (([int]$hash[$offset] -band 0x7F) -shl 24) -bor ([int]$hash[$offset + 1] -shl 16) -bor ([int]$hash[$offset + 2] -shl 8) -bor [int]$hash[$offset + 3]
    return ($binary % 1000000).ToString('D6')
}

if (-not $Settings) { $Settings = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src\ApiGatewayPay.Api\appsettings.Development.json' }
$cfg = Get-Content $Settings -Raw -Encoding UTF8 | ConvertFrom-Json
$u = $cfg.Auth.Local.Users | Where-Object { $_.Username -eq $User }
if (-not $u) { throw "Usuario no encontrado: $User" }
$secret = $cfg.DevSecrets."sandbox/$($u.TotpSecretName)"
$step = [long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30) + $StepOffset
Get-TotpCode (ConvertFrom-Base32 $secret) $step
