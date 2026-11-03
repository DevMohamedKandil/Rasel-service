# PowerShell 5.1+ example. Usage:  $env:RASEL_KEY = '<your key>'; .\rasel.ps1
$Base = if ($env:RASEL_URL) { $env:RASEL_URL } else { 'http://localhost:5114' }
$H = @{ 'X-Api-Key' = $env:RASEL_KEY }

function Invoke-Rasel($Method, $Path, $Body = $null, $Extra = @{}) {
    $args2 = @{ Uri = "$Base$Path"; Method = $Method; Headers = ($H + $Extra); ContentType = 'application/json'; UseBasicParsing = $true }
    if ($Body) { $args2.Body = ($Body | ConvertTo-Json -Compress) }
    try { return Invoke-RestMethod @args2 }
    catch {
        $resp = $_.Exception.Response
        $code = 'unknown'
        if ($resp) { $reader = New-Object IO.StreamReader($resp.GetResponseStream()); try { $code = ($reader.ReadToEnd() | ConvertFrom-Json).code } catch {} }
        "-> HTTP $([int]$resp.StatusCode) code=$code"
    }
}

$s = Invoke-Rasel Post '/v1/sessions' @{ name = 'ps-demo' }
"session $($s.id) $($s.status)"
$null = Invoke-Rasel Get "/v1/sessions/$($s.id)/qr"          # Fake provider only: connects the session
$m = Invoke-Rasel Post '/v1/messages' @{ phone = ('+2010' + (Get-Random -Minimum 10000000 -Maximum 99999999)); text = 'hello'; sessionId = $s.id } @{ 'Idempotency-Key' = "ps-$([guid]::NewGuid().ToString('N').Substring(0,8))" }
"send $($m.messageId) $($m.status)"
Start-Sleep -Seconds 2
Invoke-Rasel Get "/v1/messages/$($m.messageId)" | ConvertTo-Json -Compress
$p = '+2010' + (Get-Random -Minimum 10000000 -Maximum 99999999)
$c = Invoke-Rasel Post '/v1/otp/request' @{ phone = $p; purpose = 'login'; channel = 'whatsapp'; language = 'en' }
"otp challenge $($c.challengeId)"
Invoke-Rasel Post '/v1/otp/request' @{ phone = $p; purpose = 'login' }
Invoke-Rasel Post '/v1/otp/verify' @{ challengeId = $c.challengeId; code = '000000' }
