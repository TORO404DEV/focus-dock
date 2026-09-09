param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\FocusDock.App\bin\$Configuration\net8.0-windows\win-x64\FocusDock.exe"
$icon = Join-Path $repoRoot "src\FocusDock.App\Assets\FocusDock.ico"

if (-not (Test-Path -LiteralPath $exe)) {
    throw "No encuentro FocusDock.exe. Compila primero con: dotnet build FocusDock.sln -c $Configuration"
}

$programs = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$folder = Join-Path $programs "Focus Dock"
$shortcutPath = Join-Path $folder "Focus Dock.lnk"
New-Item -ItemType Directory -Force -Path $folder | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = Split-Path $exe
$shortcut.Description = "Focus Dock · Pomodoro y widgets para monitor secundario"
if (Test-Path -LiteralPath $icon) { $shortcut.IconLocation = "$icon,0" } else { $shortcut.IconLocation = "$exe,0" }
$shortcut.Save()

Write-Output "Acceso creado en: $shortcutPath"
Write-Output "Ahora busca Focus Dock desde el menú Inicio de Windows."
