<#
.SYNOPSIS
  Veroeffentlicht Dienst und Client als eigenstaendige exe-Dateien.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    # Self-contained = laeuft ohne vorinstallierte .NET-Runtime (groesser, aber
    # auf den Kinder-PCs ist dann keine Installation noetig).
    [bool]$SelfContained = $true
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$sc = if ($SelfContained) { "true" } else { "false" }

$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", $sc)

dotnet publish "$root\src\RunAsAdmin.Service" @common -o "$root\publish\service"
dotnet publish "$root\src\RunAsAdmin.Client"  @common -o "$root\publish\client"
dotnet publish "$root\src\RunAsAdmin.Gui"       @common -o "$root\publish\gui"
dotnet publish "$root\src\RunAsAdmin.Installer" @common -o "$root\publish\installer"

Write-Host "Veroeffentlicht nach $root\publish (self-contained=$sc)" -ForegroundColor Green
