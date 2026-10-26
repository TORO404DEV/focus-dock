param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

# Builds PomoDock Web and drops it into website/, so the folder that is uploaded to
# public_html serves the app at / and keeps the landing at /es/ and /en/.

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\PomoDock.Web\PomoDock.Web.csproj"
$publish = Join-Path $repoRoot "artifacts\web"
$website = Join-Path $repoRoot "website"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not $dotnet -or -not (& $dotnet --list-sdks 2>$null)) {
    if (Test-Path -LiteralPath $userDotnet) { $dotnet = $userDotnet }
}
if (-not $dotnet) { throw "The .NET 8 SDK is required to build PomoDock Web." }

if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
& $dotnet publish $project -c $Configuration -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$wwwroot = Join-Path $publish "wwwroot"
# Only what the build owns is replaced; the landing, assets and server files stay untouched.
foreach ($owned in @("_framework", "app")) {
    $target = Join-Path $website $owned
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $wwwroot $owned) -Destination $target -Recurse
}
Copy-Item -LiteralPath (Join-Path $wwwroot "index.html") -Destination (Join-Path $website "index.html") -Force

$size = (Get-ChildItem -LiteralPath (Join-Path $website "_framework") -Recurse -Filter *.br | Measure-Object Length -Sum).Sum
Write-Host ("PomoDock Web is in {0} ({1:N1} MB over the wire with Brotli)." -f $website, ($size / 1MB))
