<#
.SYNOPSIS
  Sucht bekannte Programme auf diesem PC und traegt die gefundenen Pfade in die
  config.json ein. VOR der Installation ausfuehren (Doppelklick oder Rechtsklick
  -> Mit PowerShell ausfuehren). Keine Adminrechte noetig.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath = "$PSScriptRoot\..\config\config.json",
    [switch]$NoPause   # beim Aufruf aus dem Installer nicht auf Tastendruck warten
)

$ErrorActionPreference = 'Stop'

function Find-First {
    param([string[]]$Candidates)
    foreach ($c in $Candidates) {
        $expanded = [Environment]::ExpandEnvironmentVariables($c)
        if (Test-Path $expanded) { return (Resolve-Path $expanded).Path }
    }
    return $null
}

# Suchkandidaten je Programm (haeufige Installationsorte)
$targets = @{
    "UniGetUI" = @(
        "$env:LOCALAPPDATA\Programs\UniGetUI\UniGetUI.exe",
        "$env:LOCALAPPDATA\Programs\WingetUI\WingetUI.exe",
        "${env:ProgramFiles}\UniGetUI\UniGetUI.exe"
    )
    "SignalRGB" = @(
        "C:\Users\Public\SignalRgb\current\SignalRgbLauncher.exe",
        "$env:LOCALAPPDATA\VortxEngine\app-*\SignalRgbLauncher.exe"
    )
    "Steam" = @(
        "${env:ProgramFiles(x86)}\Steam\steam.exe",
        "${env:ProgramFiles}\Steam\steam.exe"
    )
    "Epic Games Launcher (Fortnite)" = @(
        "${env:ProgramFiles(x86)}\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe",
        "${env:ProgramFiles}\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"
    )
}

if (-not (Test-Path $ConfigPath)) { throw "config.json nicht gefunden: $ConfigPath" }
$cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json

Write-Host "Suche Programme..." -ForegroundColor Cyan
foreach ($name in $targets.Keys) {
    $found = Find-First $targets[$name]
    $entry = $cfg.AllowedApps | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    if ($found) {
        Write-Host ("  [gefunden] {0,-32} {1}" -f $name, $found) -ForegroundColor Green
        if ($entry) { $entry.Path = $found }
    } else {
        Write-Host ("  [fehlt]    {0}" -f $name) -ForegroundColor DarkYellow
    }
}

# Schreiben
$cfg | ConvertTo-Json -Depth 8 | Set-Content $ConfigPath -Encoding UTF8
Write-Host ""
Write-Host "config.json aktualisiert: $ConfigPath" -ForegroundColor Green
Write-Host "Nicht gefundene Programme kannst du spaeter per Rechtsklick auf die .exe"
Write-Host "'Mit Adminrechten anfragen' + 'Immer erlauben' hinzufuegen."
Write-Host ""
if (-not $NoPause) {
    Write-Host "Naechster Schritt: INSTALLIEREN.cmd ausfuehren." -ForegroundColor Cyan
    pause
}
