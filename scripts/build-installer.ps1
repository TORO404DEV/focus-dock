param(
    [ValidatePattern('^\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$')]
    [string]$Version = "0.1.0",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot "artifacts"
$publish = Join-Path $artifacts "publish\win-x64"
$project = Join-Path $repoRoot "src\PomoDock.App\PomoDock.App.csproj"
$iss = Join-Path $repoRoot "installer\PomoDock.iss"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not $dotnet -or -not (& $dotnet --list-sdks 2>$null)) {
    if (Test-Path -LiteralPath $userDotnet) { $dotnet = $userDotnet }
}
if (-not $dotnet) { throw "The .NET 8 SDK is required to build PomoDock." }

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $iscc) { throw "Inno Setup 6 is required. Install JRSoftware.InnoSetup with winget." }

if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publish | Out-Null
& $dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$env:POMODOCK_VERSION = $Version
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

$installer = Join-Path $artifacts "PomoDock-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer was not created: $installer" }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash.ToLowerInvariant()
$checksum = Join-Path $artifacts "PomoDock-Setup-$Version.sha256"
Set-Content -LiteralPath $checksum -Value "$hash  PomoDock-Setup-$Version.exe" -NoNewline

Write-Output "Installer: $installer"
Write-Output "SHA256:    $hash"
