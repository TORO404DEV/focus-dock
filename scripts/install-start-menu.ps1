param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repoRoot "artifacts\publish\win-x64"
$install = Join-Path $env:LOCALAPPDATA "Programs\PomoDock"
$icon = Join-Path $repoRoot "src\PomoDock.App\Assets\PomoDock.ico"
$sourceExe = Join-Path $publish "PomoDock.exe"
if (-not (Test-Path -LiteralPath $sourceExe)) {
    $sourceExe = Join-Path $repoRoot "src\PomoDock.App\bin\$Configuration\net8.0-windows\win-x64\PomoDock.exe"
    $publish = Split-Path $sourceExe
}
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "No encuentro PomoDock.exe. Compila o publica primero."
}

New-Item -ItemType Directory -Force -Path $install | Out-Null
# Wipe first: Copy-Item alone leaves stale native backends (old ggml-vulkan.dll) that
# LLamaSharp may still load and hang/crash the agent.
Get-ChildItem -LiteralPath $install -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $publish "*") -Destination $install -Recurse -Force
# Agent is CPU-only: delete ggml-vulkan.dll that sits beside llama.dll (leave Whisper alone).
Get-ChildItem -LiteralPath $install -Recurse -Force -Filter "llama.dll" -ErrorAction SilentlyContinue | ForEach-Object {
    $sibling = Join-Path $_.DirectoryName "ggml-vulkan.dll"
    if (Test-Path -LiteralPath $sibling) { Remove-Item -LiteralPath $sibling -Force }
}

$exe = Join-Path $install "PomoDock.exe"
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
$shortcut.WorkingDirectory = $install
$shortcut.Description = "PomoDock · Pomodoro y widgets para monitor secundario"
if (Test-Path -LiteralPath $icon) { $shortcut.IconLocation = "$icon,0" } else { $shortcut.IconLocation = "$exe,0" }
$shortcut.Save()

Write-Output "Instalado en: $exe"
Write-Output "Acceso: $shortcutPath"
Write-Output "Ahora busca PomoDock desde el menú Inicio de Windows."
