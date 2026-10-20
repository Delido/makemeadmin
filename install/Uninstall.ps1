<#
.SYNOPSIS
  Entfernt RunAsAdmin vollstaendig. Als Administrator ausfuehren.
  Die Protokolle unter ProgramData bleiben erhalten (Beweissicherung),
  ausser -RemoveLogs wird angegeben.
#>
[CmdletBinding()]
param([switch]$RemoveLogs)

$ErrorActionPreference = 'SilentlyContinue'
$ServiceName = "RunAsAdmin"
$InstallDir  = "$env:ProgramFiles\RunAsAdmin"
$DataDir     = "$env:ProgramData\RunAsAdmin"

Write-Host "RunAsAdmin wird entfernt..."

# Dienst stoppen + loeschen
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -Force
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Dienst entfernt."
}

# GUI/Tray-Prozesse beenden
Get-Process RunAsAdmin.Gui -ErrorAction SilentlyContinue | Stop-Process -Force

# Verknuepfungen
$sm = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs"
Remove-Item "$sm\Programme starten (RunAsAdmin).lnk" -Force
Remove-Item "$sm\StartUp\RunAsAdmin Tray.lnk" -Force

# Rechtsklick-Eintrag
Remove-Item "Registry::HKEY_CLASSES_ROOT\exefile\shell\RunAsAdminRequest" -Recurse -Force

# URL-ACLs (Ports aus config lesen, falls vorhanden)
try {
    if (Test-Path "$DataDir\config.json") {
        $cfg = Get-Content "$DataDir\config.json" -Raw | ConvertFrom-Json
        $p = [int]$cfg.HomeAssistant.CallbackPort
        netsh http delete urlacl url="http://+:$p/decision/" | Out-Null
        netsh http delete urlacl url=("http://+:" + ($p+1) + "/trigger/") | Out-Null
        netsh advfirewall firewall delete rule name="RunAsAdmin decision ($p)" | Out-Null
        netsh advfirewall firewall delete rule name=("RunAsAdmin trigger (" + ($p+1) + ")") | Out-Null
    }
} catch {}

# Programmdateien
Remove-Item $InstallDir -Recurse -Force

# Ereignisquelle
if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::DeleteEventSource($ServiceName)
}

if ($RemoveLogs) {
    Remove-Item $DataDir -Recurse -Force
    Write-Host "Protokolle entfernt."
} else {
    Write-Host "Protokolle bleiben erhalten: $DataDir"
}

Write-Host "RunAsAdmin wurde entfernt." -ForegroundColor Green
