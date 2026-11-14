# Builds a self-contained release (no .NET install needed on the target PC) and, if Inno Setup 6
# is installed, the installer. Usage: powershell -File tools\publish.ps1 [-SkipInstaller]
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\LCapture\LCapture.csproj"
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$version = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:Platform=x64 -p:PublishReadyToRun=true -o $publishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
"Publicado LCapture $version em $publishDir"

if ($SkipInstaller) { return }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup 6 não encontrado (winget install JRSoftware.InnoSetup). Instalador não gerado."
    return
}

& $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" (Join-Path $root "installer\LCapture.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
