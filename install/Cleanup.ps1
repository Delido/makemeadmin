<#
.SYNOPSIS
  Radikale Bereinigung von RunAsAdmin. Entfernt Dienst, Prozesse und Programmdateien
  wasserdicht – auch wenn eine Datei gesperrt ist (dann Loeschung beim Neustart).
  Als Administrator ausfuehren. Protokolle/Konfiguration bleiben erhalten
  (fuer komplette Entfernung: -All).
#>
[CmdletBinding()]
param([switch]$All)

$ErrorActionPreference = 'Continue'
$svc = "RunAsAdmin"
$installDir = "$env:ProgramFiles\RunAsAdmin"
$dataDir    = "$env:ProgramData\RunAsAdmin"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host "Bitte als Administrator ausfuehren!" -ForegroundColor Red; exit 1
    }
}
Assert-Admin

Write-Host "== RunAsAdmin wird bereinigt ==" -ForegroundColor Cyan

# 1) Dienst stoppen + loeschen ALS SYSTEM (Dienst ist gegen Admin-Stop gehaertet).
Write-Host "Stoppe Dienst (als SYSTEM)..."
$op = "$env:TEMP\raa_cleanup_sysop.cmd"
Set-Content -Path $op -Value "@echo off`r`nsc.exe stop RunAsAdmin`r`ntimeout /t 3 /nobreak >nul`r`nsc.exe delete RunAsAdmin`r`n" -Encoding OEM
schtasks /Create /TN RunAsAdmin_Cleanup /TR "cmd /c `"$op`"" /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /F | Out-Null
schtasks /Run /TN RunAsAdmin_Cleanup | Out-Null
Start-Sleep -Seconds 5
schtasks /Delete /TN RunAsAdmin_Cleanup /F 2>$null | Out-Null
Remove-Item $op -Force -ErrorAction SilentlyContinue
# Fallback (falls schon entsperrt):
sc.exe stop $svc 2>$null | Out-Null

# 2) Prozesse wasserdicht beenden (bis zu 20x)
for ($i = 0; $i -lt 20; $i++) {
    $procs = Get-Process RunAsAdmin.Service, RunAsAdmin.Gui -ErrorAction SilentlyContinue
    if (-not $procs) { break }
    $procs | ForEach-Object { try { $_.Kill(); $_.WaitForExit(1000) } catch {} }
    Start-Sleep -Milliseconds 500
}
# Zur Sicherheit per taskkill
taskkill /im RunAsAdmin.Service.exe /f 2>$null | Out-Null
taskkill /im RunAsAdmin.Gui.exe /f 2>$null | Out-Null
Start-Sleep -Seconds 1

# 3) Dienst loeschen
Write-Host "Entferne Dienst-Registrierung..."
sc.exe delete $svc | Out-Null
Start-Sleep -Seconds 1

# 4) Programmdateien loeschen (gesperrte -> beim Neustart)
Add-Type -Namespace Win32 -Name Native -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)]
public static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
"@
$MOVEFILE_DELAY_UNTIL_REBOOT = 0x4
$rebootNeeded = $false

if (Test-Path $installDir) {
    Write-Host "Loesche Programmdateien..."
    try {
        Remove-Item $installDir -Recurse -Force -ErrorAction Stop
        Write-Host "  Programmdateien entfernt." -ForegroundColor Green
    } catch {
        # Einzelne gesperrte Dateien zum Loeschen beim Neustart vormerken
        Get-ChildItem $installDir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item $_.FullName -Force -ErrorAction Stop }
            catch {
                [Win32.Native]::MoveFileEx($_.FullName, $null, $MOVEFILE_DELAY_UNTIL_REBOOT) | Out-Null
                $script:rebootNeeded = $true
            }
        }
        try { Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        [Win32.Native]::MoveFileEx($installDir, $null, $MOVEFILE_DELAY_UNTIL_REBOOT) | Out-Null
    }
}

# 5) Verknuepfungen + Rechtsklick + Ereignisquelle
$sm = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs"
Remove-Item "$sm\Programme starten (RunAsAdmin).lnk" -Force -ErrorAction SilentlyContinue
Remove-Item "$sm\StartUp\RunAsAdmin Tray.lnk" -Force -ErrorAction SilentlyContinue
Remove-Item "Registry::HKEY_CLASSES_ROOT\exefile\shell\RunAsAdminRequest" -Recurse -Force -ErrorAction SilentlyContinue
try { if ([System.Diagnostics.EventLog]::SourceExists($svc)) { [System.Diagnostics.EventLog]::DeleteEventSource($svc) } } catch {}

# 6) URL-ACLs + Firewall
netsh http delete urlacl url=http://+:8731/decision/ 2>$null | Out-Null
netsh http delete urlacl url=http://+:8732/trigger/ 2>$null | Out-Null
netsh advfirewall firewall delete rule name="RunAsAdmin decision (8731)" 2>$null | Out-Null
netsh advfirewall firewall delete rule name="RunAsAdmin trigger (8732)" 2>$null | Out-Null

# 7) Optional: Daten/Protokolle
if ($All) {
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Konfiguration und Protokolle entfernt." -ForegroundColor Yellow
} else {
    Write-Host "Konfiguration/Protokolle bleiben erhalten: $dataDir"
}

Write-Host ""
if ($rebootNeeded) {
    Write-Host "FAST FERTIG: Einige Dateien waren gesperrt und werden beim NEUSTART entfernt." -ForegroundColor Yellow
    Write-Host "Bitte den PC neu starten, DANN neu installieren." -ForegroundColor Yellow
} else {
    Write-Host "FERTIG. RunAsAdmin wurde vollstaendig entfernt. Du kannst jetzt neu installieren." -ForegroundColor Green
}
