<#
.SYNOPSIS
  Testet die RunAsAdmin-Freigabe in Home Assistant, ohne Kinder-PC.
  Schickt eine Beispiel-Anfrage an den Webhook. Auf dem Handy sollte eine
  Benachrichtigung mit "Erlauben"/"Ablehnen" erscheinen.
.NOTES
  Der Rueckkanal (CallbackUrl) zeigt bewusst auf diesen PC. Ohne laufenden Dienst
  laeuft die Entscheidung am Handy ins Leere - fuer den reinen Push-Test ist das ok.
#>
param(
    [Parameter(Mandatory=$true)][string]$BaseUrl,   # z. B. https://dein-ha.example.com
    [string]$Webhook = "runasadmin_request",
    [Parameter(Mandatory=$true)][string]$Secret     # dein CallbackSecret
)

$body = @{
    RequestId   = [guid]::NewGuid().ToString('N')
    Computer    = "FME-PC"
    User        = "FME-PC\kind"
    AppName     = "SignalRGB (TEST)"
    Path        = "C:\Test\SignalRGB.exe"
    Reason      = "Testlauf der Freigabe"
    Secret      = $Secret
    CallbackUrl = "http://$($env:COMPUTERNAME):8731/decision/"
} | ConvertTo-Json -Compress

$uri = "$($BaseUrl.TrimEnd('/'))/api/webhook/$Webhook"
Write-Host "POST $uri"
try {
    Invoke-RestMethod -Uri $uri -Method Post -Body $body -ContentType "application/json" -TimeoutSec 15
    Write-Host "Gesendet. Schau auf dein Handy -> Benachrichtigung mit Erlauben/Ablehnen." -ForegroundColor Green
} catch {
    Write-Host "Fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor Red
}
