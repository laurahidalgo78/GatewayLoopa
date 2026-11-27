<#
.SYNOPSIS
  Demostración de punta a punta contra la API en desarrollo (Mock de backend + Mock de banco).
  Requiere la API corriendo:  dotnet run --project src/ApiGatewayPay.Api
.EXAMPLE
  ./tools/demo-e2e.ps1
#>
param([string]$BaseUrl = "http://localhost:8080")
$ErrorActionPreference = 'Stop'
$Password = 'DevOnly-Sandbox-2026!'
$totp = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Get-Totp.ps1'

function Call([string]$Method, [string]$Path, $Token, $Body = $null, [hashtable]$Headers = @{}) {
    $h = @{} + $Headers
    if ($Token) { $h['Authorization'] = "Bearer $Token" }
    $params = @{ Method = $Method; Uri = "$BaseUrl$Path"; Headers = $h; UseBasicParsing = $true }
    if ($null -ne $Body) { $params['Body'] = ($Body | ConvertTo-Json -Depth 5); $params['ContentType'] = 'application/json' }
    try {
        $r = Invoke-WebRequest @params
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Headers = $r.Headers; Body = ($r.Content | ConvertFrom-Json) }
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Headers = $resp.Headers; Body = ($reader.ReadToEnd() | ConvertFrom-Json) }
    } catch {
        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
            return [pscustomobject]@{ Status = $status; Headers = $_.Exception.Response.Headers; Body = ($_.ErrorDetails.Message | ConvertFrom-Json) }
        }
        throw
    }
}

function Login([string]$User, [switch]$Mfa) {
    # Un código TOTP es de un solo uso: si el de esta ventana ya se usó, se prueba el de la siguiente (tolerancia ±1).
    foreach ($offset in 0, 1) {
        $form = @{ grant_type = 'password'; username = $User; password = $Password }
        if ($Mfa) { $form['otp'] = & $totp -User $User -StepOffset $offset }
        try { return (Invoke-RestMethod -Method Post -Uri "$BaseUrl/v1/auth/token" -Body $form -ContentType 'application/x-www-form-urlencoded').access_token }
        catch { if (-not $Mfa -or $offset -eq 1) { throw } }
    }
}

function WaitStatus($Token, [string]$Id, [string[]]$Expected, [int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $p = (Call GET "/v1/payments/$Id" $Token).Body
        if ($Expected -contains $p.status) { return $p }
        Start-Sleep -Milliseconds 700
    } while ((Get-Date) -lt $deadline)
    throw "El pago $Id no llegó a $($Expected -join '/') (estado: $($p.status))"
}

function Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }
function Ok([string]$Text) { Write-Host "   OK  $Text" -ForegroundColor Green }

Step "Login maker (contraseña + TOTP) y checker (contraseña + TOTP)"
$maker = Login 'maker@tenant-a' -Mfa
$checker = Login 'checker@tenant-a' -Mfa
Ok "tokens emitidos (JWT ES256, 5 min)"

Step "Registrar beneficiario (requiere MFA) y aprobarlo con otro usuario (maker-checker)"
$acct = '{0:D10}' -f (Get-Random -Minimum 1000000000 -Maximum 2147483647)
$b = Call POST '/v1/beneficiaries' $maker @{ displayName = 'Proveedor Demo SAS'; bankCode = '1007'; accountType = 'savings'; accountNumber = $acct }
Ok "beneficiario $($b.Body.id) estado=$($b.Body.status) cuenta=$($b.Body.accountNumberMasked)"
$selfApprove = Call POST "/v1/beneficiaries/$($b.Body.id)/approve" $maker
Ok "auto-aprobación por el maker rechazada: HTTP $($selfApprove.Status) $($selfApprove.Body.code)"
$approved = Call POST "/v1/beneficiaries/$($b.Body.id)/approve" $checker
Ok "aprobado por checker: estado=$($approved.Body.status)"

Step "Pago normal con Idempotency-Key + reintento del cliente"
$key = [guid]::NewGuid().ToString('N')
$body = @{ beneficiaryId = $b.Body.id; amountMinor = 15000000; currency = 'COP'; reference = 'Factura 1001' }
$p1 = Call POST '/v1/payments' $maker $body @{ 'Idempotency-Key' = $key }
$p2 = Call POST '/v1/payments' $maker $body @{ 'Idempotency-Key' = $key }
Ok "creado $($p1.Body.id) estado=$($p1.Body.status); reintento devolvió el mismo id: $($p1.Body.id -eq $p2.Body.id) (Idempotent-Replayed=$($p2.Headers['Idempotent-Replayed']))"
$changed = $body.Clone(); $changed['amountMinor'] = 15000001
$p3 = Call POST '/v1/payments' $maker $changed @{ 'Idempotency-Key' = $key }
Ok "misma clave con otro contenido: HTTP $($p3.Status) $($p3.Body.code)"
$final = WaitStatus $maker $p1.Body.id @('confirmed')
Ok "confirmado por el banco simulado, bankReference=$($final.bankReference)"

Step "Pago de monto alto -> doble aprobación"
$big = Call POST '/v1/payments' $maker @{ beneficiaryId = $b.Body.id; amountMinor = 1500000000; currency = 'COP'; reference = 'Compra equipos' } @{ 'Idempotency-Key' = [guid]::NewGuid().ToString('N') }
Ok "estado=$($big.Body.status) riskFlags=$($big.Body.riskFlags -join ',')"
$makerApprove = Call POST "/v1/payments/$($big.Body.id)/approve" $maker
Ok "maker intenta aprobar: HTTP $($makerApprove.Status) $($makerApprove.Body.code)"
$checkerApprove = Call POST "/v1/payments/$($big.Body.id)/approve" $checker
Ok "checker aprueba: estado=$($checkerApprove.Body.status)"
WaitStatus $maker $big.Body.id @('confirmed') | Out-Null
Ok "confirmado"

Step "Timeout ambiguo del banco (monto terminado en 99): nunca se reenvía a ciegas"
$amb = Call POST '/v1/payments' $maker @{ beneficiaryId = $b.Body.id; amountMinor = 2500099; currency = 'COP'; reference = 'Prueba timeout' } @{ 'Idempotency-Key' = [guid]::NewGuid().ToString('N') }
$r = WaitStatus $maker $amb.Body.id @('confirmed', 'failed') 90
Ok "resuelto por consulta de estado: $($r.status)"

Step "Rechazo del banco (monto terminado en 13)"
$rej = Call POST '/v1/payments' $maker @{ beneficiaryId = $b.Body.id; amountMinor = 3000013; currency = 'COP'; reference = 'Prueba rechazo' } @{ 'Idempotency-Key' = [guid]::NewGuid().ToString('N') }
$r = WaitStatus $maker $rej.Body.id @('failed')
Ok "estado=$($r.status) failureCode=$($r.failureCode)"

Step "Seguridad: BOLA entre tenants, campos desconocidos, sin token"
$other = Login 'maker@tenant-b' -Mfa
$bola = Call GET "/v1/payments/$($p1.Body.id)" $other
Ok "tenant-b leyendo pago de tenant-a: HTTP $($bola.Status) $($bola.Body.code)"
$unknown = Call POST '/v1/payments' $maker @{ beneficiaryId = $b.Body.id; amountMinor = 100; currency = 'COP'; reference = 'x'; status = 'confirmed' } @{ 'Idempotency-Key' = [guid]::NewGuid().ToString('N') }
Ok "campo extra 'status': HTTP $($unknown.Status) $($unknown.Body.code)"
$anon = Call GET '/v1/payments' $null
Ok "sin token: HTTP $($anon.Status) $($anon.Body.code)"

Step "Saldo y movimientos (alias opaco, número enmascarado)"
$accounts = (Call GET '/v1/accounts' $maker).Body
$bal = (Call GET '/v1/accounts/ops-cop/balance' $maker).Body
Ok "$($accounts[0].accountRef) $($accounts[0].accountNumberMasked) disponible=$($bal.availableMinor) $($bal.currency)"

Step "Operaciones de plataforma: conciliación y verificación del libro"
$ops = Login 'ops@platform' -Mfa
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
$rec = (Call POST '/v1/admin/reconciliation/runs' $ops @{ date = $today }).Body
Ok "conciliación $($rec.date): internos=$($rec.internalCount) banco=$($rec.bankCount) discrepancias=$($rec.discrepancies.Count)"
$admin = Login 'admin@tenant-a'
$ver = (Call GET '/v1/admin/ledger/verify' $admin).Body
Ok "libro inmutable: valid=$($ver.valid) agregados=$($ver.aggregatesChecked) entradas=$($ver.entriesChecked)"

Step "Kill switch"
$ks = Call POST '/v1/admin/kill-switch' $ops @{ active = $true; reason = 'Simulacro de incidente' }
$blocked = Call POST '/v1/payments' $maker $body @{ 'Idempotency-Key' = [guid]::NewGuid().ToString('N') }
Ok "kill switch activo=$($ks.Body.active); nuevo pago: HTTP $($blocked.Status) $($blocked.Body.code)"
Call POST '/v1/admin/kill-switch' $ops @{ active = $false; reason = 'Fin del simulacro' } | Out-Null
Ok "kill switch desactivado"

Write-Host "`nDemo completada." -ForegroundColor Green
