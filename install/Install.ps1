<#
.SYNOPSIS
  Installiert den RunAsAdmin-Dienst auf einem Kinder-PC.
.DESCRIPTION
  Muss als Administrator ausgefuehrt werden. Kopiert die Programmdateien nach
  Program Files, legt die Konfiguration und die Protokolle unter ProgramData an,
  setzt restriktive Berechtigungen (Kinder duerfen nur lesen bzw. gar nichts),
  erstellt die Ereignisprotokoll-Quelle und registriert den Windows-Dienst als
  LocalSystem.
.PARAMETER PublishDir
  Verzeichnis mit den veroeffentlichten Dateien (dotnet publish des Service-Projekts).
.PARAMETER ConfigSource
  Pfad zur config.json, die installiert werden soll.
#>
[CmdletBinding()]
param(
    # Optional explizit setzbar; sonst automatisch aus dem Paket ermittelt.
    [string]$PublishDir,
    [string]$ConfigSource,
    [string]$GuiPublishDir,
    # Vor der Installation die Programmpfade suchen und in die config eintragen.
    [bool]$FindPaths = $true
)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Bitte als Administrator ausfuehren (INSTALLIEREN.cmd verwenden)."
    }
}
Assert-Admin

# --- Paketwurzel robust bestimmen (install/ liegt neben publish/ und config/) ---
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PkgRoot = Split-Path -Parent $ScriptDir
if (-not (Test-Path (Join-Path $PkgRoot "publish"))) {
    # Fallback: vielleicht liegt publish direkt neben dem Skript
    if (Test-Path (Join-Path $ScriptDir "publish")) { $PkgRoot = $ScriptDir }
}

if (-not $PublishDir)       { $PublishDir       = Join-Path $PkgRoot "publish\service" }
if (-not $GuiPublishDir)    { $GuiPublishDir    = Join-Path $PkgRoot "publish\gui" }
if (-not $ConfigSource)     { $ConfigSource     = Join-Path $PkgRoot "config\config.json" }

Write-Host "Paketordner: $PkgRoot"
if (-not (Test-Path $PublishDir)) {
    throw "Die Programmdateien wurden nicht gefunden unter: $PublishDir`n" +
          "Bitte den KOMPLETTEN Paketordner kopieren (publish + install + config)."
}

# --- Programmpfade suchen und in die config eintragen (ohne separaten Klick) ---
if ($FindPaths) {
    $finder = Join-Path $ScriptDir "1_Pfade-finden.ps1"
    if (Test-Path $finder) {
        Write-Host "Suche installierte Programme..."
        try { & $finder -ConfigPath $ConfigSource -NoPause } catch { Write-Warning "Pfadsuche uebersprungen: $_" }
    }
}

$InstallDir  = "$env:ProgramFiles\RunAsAdmin"
$GuiDir      = "$env:ProgramFiles\RunAsAdmin\Gui"
$DataDir     = "$env:ProgramData\RunAsAdmin"
$LogDir      = "$DataDir\logs"
$ConfigDest  = "$DataDir\config.json"
$ServiceName = "RunAsAdmin"
$ServiceExe  = "$InstallDir\RunAsAdmin.Service.exe"

Write-Host "Installationsverzeichnis: $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir, $GuiDir, $DataDir, $LogDir | Out-Null

# --- Dateien kopieren ---
if (-not (Test-Path $PublishDir)) { throw "PublishDir nicht gefunden: $PublishDir. Erst Publish.ps1 ausfuehren." }
Copy-Item "$PublishDir\*" $InstallDir -Recurse -Force
if (Test-Path $GuiPublishDir)    { Copy-Item "$GuiPublishDir\*"    $GuiDir    -Recurse -Force }

# --- Konfiguration installieren (nicht ueberschreiben, falls vorhanden) ---
if (-not (Test-Path $ConfigDest)) {
    if (-not (Test-Path $ConfigSource)) { throw "ConfigSource nicht gefunden: $ConfigSource" }
    Copy-Item $ConfigSource $ConfigDest -Force
    Write-Host "Konfiguration installiert: $ConfigDest"
} else {
    Write-Host "Konfiguration existiert bereits, bleibt unveraendert: $ConfigDest"
}

# --- Ereignisprotokoll-Quelle ---
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, "Application")
    Write-Host "Ereignisquelle '$ServiceName' angelegt."
}

# --- Berechtigungen haerten ---
# ProgramData\RunAsAdmin: Vererbung aus, SYSTEM+Administratoren Vollzugriff,
# Benutzer nur Lesen (fuer config.json). Logs: Benutzer KEIN Zugriff.
function Set-HardenedAcl {
    param([string]$Path, [switch]$DenyUsers)
    $acl = Get-Acl $Path
    $acl.SetAccessRuleProtection($true, $false)   # Vererbung deaktivieren, geerbte nicht uebernehmen
    $acl.Access | ForEach-Object { [void]$acl.RemoveAccessRule($_) }

    $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
    $inh = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit,ObjectInherit"
    $prop = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow

    foreach ($sid in @("S-1-5-18", "S-1-5-32-544")) {  # SYSTEM, Administratoren
        $id = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $id, $rights, $inh, $prop, $allow)))
    }

    if (-not $DenyUsers) {
        # Authentifizierte Benutzer: nur Lesen/Ausfuehren (config.json lesbar fuer GUI)
        $usersSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-11")
        $read = [System.Security.AccessControl.FileSystemRights]"ReadAndExecute"
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $usersSid, $read, $inh, $prop, $allow)))
    }
    Set-Acl -Path $Path -AclObject $acl
}

Set-HardenedAcl -Path $DataDir
Set-HardenedAcl -Path $LogDir -DenyUsers   # Kinder duerfen Protokolle nicht lesen/aendern
Write-Host "Berechtigungen gesetzt (Kinder koennen Konfiguration und Protokolle nicht aendern)."

# --- URL-Vorbehalte fuer die HTTP-Listener (laufen als LocalSystem) ---
# CallbackPort (HA-Entscheidung) und CallbackPort+1 (Remote-Trigger).
try {
    $cfg = Get-Content $ConfigDest -Raw | ConvertFrom-Json
    $cbPort = [int]$cfg.HomeAssistant.CallbackPort
    foreach ($p in @($cbPort, $cbPort + 1)) {
        $path = if ($p -eq $cbPort) { "decision" } else { "trigger" }
        $url = "http://+:$p/$path/"
        netsh http add urlacl url=$url user="NT AUTHORITY\SYSTEM" 2>&1 | Out-Null
        netsh advfirewall firewall add rule name="RunAsAdmin $path ($p)" dir=in action=allow `
            protocol=TCP localport=$p profile=any 2>&1 | Out-Null
    }
    Write-Host "URL-Vorbehalte und Firewallregeln (privates Netz) gesetzt."
} catch {
    Write-Warning "URL-Vorbehalte konnten nicht gesetzt werden: $_"
}

# --- GUI: Startmenue-Verknuepfung + Autostart (Tray) fuer alle Benutzer ---
try {
    $guiExe = "$GuiDir\RunAsAdmin.Gui.exe"
    $shell = New-Object -ComObject WScript.Shell

    # Startmenue (alle Benutzer)
    $startMenu = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs"
    $lnk = $shell.CreateShortcut("$startMenu\Programme starten (RunAsAdmin).lnk")
    $lnk.TargetPath = $guiExe
    $lnk.IconLocation = "$guiExe,0"
    $lnk.Save()

    # Autostart als Tray (alle Benutzer) – GUI startet minimiert ins Tray
    $startup = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp"
    $al = $shell.CreateShortcut("$startup\RunAsAdmin Tray.lnk")
    $al.TargetPath = $guiExe
    $al.Arguments = "/tray"
    $al.IconLocation = "$guiExe,0"
    $al.Save()

    Write-Host "GUI-Verknuepfung + Autostart (Tray) eingerichtet."
} catch {
    Write-Warning "GUI-Verknuepfungen konnten nicht gesetzt werden: $_"
}

# --- Explorer-Rechtsklick auf .exe: "Mit Adminrechten anfragen" ---
# Ruft die GUI auf, die die Anfrage an den Dienst schickt.
try {
    $guiExe = "$GuiDir\RunAsAdmin.Gui.exe"
    $key = "Registry::HKEY_CLASSES_ROOT\exefile\shell\RunAsAdminRequest"
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(Default)" -Value "Mit Adminrechten anfragen"
    Set-ItemProperty -Path $key -Name "Icon" -Value "$guiExe,0"
    New-Item -Path "$key\command" -Force | Out-Null
    # Grafischer Dialog statt Konsole (kein leeres Shell-Fenster mehr).
    Set-ItemProperty -Path "$key\command" -Name "(Default)" -Value "`"$guiExe`" --request `"%1`""
    Write-Host "Explorer-Rechtsklick 'Mit Adminrechten anfragen' eingerichtet."
} catch {
    Write-Warning "Rechtsklick-Eintrag konnte nicht gesetzt werden: $_"
}

# --- Dienst registrieren ---
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Dienst existiert bereits, wird gestoppt und aktualisiert."
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

New-Service -Name $ServiceName -BinaryPathName "`"$ServiceExe`"" `
    -DisplayName "RunAsAdmin (kontrollierte Rechteerhoehung)" `
    -Description "Startet freigegebene Programme fuer Standardbenutzer kontrolliert und protokolliert erhoeht." `
    -StartupType Automatic | Out-Null

# Laeuft als LocalSystem (Standard bei New-Service).
Start-Service $ServiceName
Write-Host "Dienst '$ServiceName' installiert und gestartet." -ForegroundColor Green
Write-Host "Protokolle: $LogDir"
