# Builds a self-contained release (no .NET install needed on the target PC) and, if Inno Setup 6
# is installed, the installer. Usage: powershell -File tools\publish.ps1 [-SkipInstaller]
#
# Code signing (Azure Trusted Signing), optional: pass -SignMetadata <metadata.json> and
# -SignDlib <Azure.CodeSigning.Dlib.dll>. Requires signtool.exe (Windows SDK) and an Azure
# login (az login). See README, "Assinatura digital".
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller,
    [string]$SignMetadata,
    [string]$SignDlib
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\LCapture\LCapture.csproj"
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$version = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$sign = [bool]$SignMetadata

function Invoke-Sign([string[]]$Files) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe não encontrado (instale o Windows SDK)." }
    if (-not (Test-Path $SignDlib)) { throw "Azure.CodeSigning.Dlib.dll não encontrado em '$SignDlib'." }

    & $signtool.FullName sign /fd SHA256 /tr "http://timestamp.acs.microsoft.com" /td SHA256 `
        /dlib $SignDlib /dmdf $SignMetadata @Files
    if ($LASTEXITCODE -ne 0) { throw "Falha ao assinar: $($Files -join ', ')" }
}

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:Platform=x64 -p:PublishReadyToRun=true -o $publishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
"Publicado LCapture $version em $publishDir"

# The .NET runtime files are already signed by Microsoft; sign only our own binaries and the
# third-party ones that ship unsigned.
if ($sign) {
    Invoke-Sign (Get-ChildItem $publishDir -File | Where-Object { ($_.Name -like "LCapture.*" -and $_.Extension -in ".exe", ".dll") -or $_.Name -like "NAudio*.dll" } |
        ForEach-Object FullName)
}

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

if ($sign) {
    Invoke-Sign @(Join-Path $root "installer\Output\LCapture-Setup-$version.exe")
}
