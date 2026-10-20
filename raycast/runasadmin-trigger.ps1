#!/usr/bin/env pwsh
# @raycast.schemaVersion 1
# @raycast.title Admin-Start auf Kinder-PC
# @raycast.mode compact
# @raycast.packageName RunAsAdmin
# @raycast.icon 🛡️
# @raycast.argument1 { "type": "dropdown", "placeholder": "PC", "data": [ { "title": "FME-PC", "value": "192.168.178.135" }, { "title": "HME-PC", "value": "192.168.178.100" } ] }
# @raycast.argument2 { "type": "text", "placeholder": "App-Name (z. B. Steam)" }
#
# Documentation:
# @raycast.description Startet ein freigegebenes Programm auf einem Kinder-PC erhoeht (ueber den RunAsAdmin-Dienst).
# @raycast.author Sebastian

param(
    [Parameter(Mandatory = $true)][string]$Host,
    [Parameter(Mandatory = $true)][string]$AppName
)

# Secret aus Umgebungsvariable lesen (NICHT ins Skript schreiben).
# In Raycast: Settings -> Extensions -> Script Commands -> Environment, oder
# dauerhaft:  setx RUNASADMIN_SECRET "dein-geheimnis"
$secret = $env:RUNASADMIN_SECRET
if (-not $secret) {
    Write-Output "Fehler: Umgebungsvariable RUNASADMIN_SECRET ist nicht gesetzt."
    exit 1
}

$port = 8732   # CallbackPort (8731) + 1
$uri  = "http://$Host`:$port/trigger/"
$body = @{ AppName = $AppName; Operator = $env:USERNAME; Secret = $secret } | ConvertTo-Json -Compress

try {
    $resp = Invoke-RestMethod -Uri $uri -Method Post -Body $body -ContentType "application/json" -TimeoutSec 15
    Write-Output $resp.Message
} catch {
    Write-Output "Fehlgeschlagen: $($_.Exception.Message)"
    exit 1
}
