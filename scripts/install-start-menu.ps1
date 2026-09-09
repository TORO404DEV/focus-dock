param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\PomoDock.App\bin\$Configuration\net8.0-windows\win-x64\PomoDock.exe"
$icon = Join-Path $repoRoot "src\PomoDock.App\Assets\PomoDock.ico"

if (-not (Test-Path -LiteralPath $exe)) {
    throw "No encuentro PomoDock.exe. Compila primero con: dotnet build PomoDock.sln -c $Configuration"
}

$programs = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$legacyFolder = Join-Path $programs "Focus Dock"
$legacyShortcut = Join-Path $legacyFolder "Focus Dock.lnk"
if (Test-Path -LiteralPath $legacyShortcut) { Remove-Item -LiteralPath $legacyShortcut -Force }
if ((Test-Path -LiteralPath $legacyFolder) -and -not (Get-ChildItem -LiteralPath $legacyFolder -Force)) { Remove-Item -LiteralPath $legacyFolder -Force }
$folder = Join-Path $programs "PomoDock"
$shortcutPath = Join-Path $folder "PomoDock.lnk"
New-Item -ItemType Directory -Force -Path $folder | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = Split-Path $exe
$shortcut.Description = "PomoDock · Pomodoro y widgets para monitor secundario"
if (Test-Path -LiteralPath $icon) { $shortcut.IconLocation = "$icon,0" } else { $shortcut.IconLocation = "$exe,0" }
$shortcut.Save()

Write-Output "Acceso creado en: $shortcutPath"
Write-Output "Ahora busca PomoDock desde el menú Inicio de Windows."
