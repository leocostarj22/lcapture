# Builds the MSIX package for the Microsoft Store (artifacts\msix\LCapture_<versão>_x64.msix).
# The Store signs the package itself, so the upload needs no certificate. -TestSign signs it with
# a self-signed test certificate so it can be installed on this PC before submitting.
#
# Usage: powershell -File tools\package-msix.ps1 [-TestSign]
#        [-IdentityName ... -Publisher "CN=..." -PublisherDisplayName ...]   (Partner Center > Product identity)
param(
    [string]$IdentityName = "LCapture",
    [string]$Publisher = "CN=leocostarj22",
    [string]$PublisherDisplayName = "leocostarj22",
    [string]$Language = "pt-PT",
    [switch]$TestSign
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\LCapture\LCapture.csproj"
$outDir = Join-Path $root "artifacts\msix"
$layout = Join-Path $outDir "layout"

# MSIX versions have four parts; the Store requires the last one to be 0.
$appVersion = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$version = "$appVersion.0"

$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 | ForEach-Object DirectoryName
if (-not $sdkBin) { throw "makeappx.exe não encontrado (instale o Windows SDK)." }

if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:Platform=x64 -p:PublishReadyToRun=true -o $layout
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -Recurse (Join-Path $root "packaging\Assets") (Join-Path $layout "Assets")

$manifest = (Get-Content -Raw -Encoding UTF8 (Join-Path $root "packaging\AppxManifest.template.xml")).
    Replace('$IdentityName$', $IdentityName).
    Replace('$Publisher$', [System.Security.SecurityElement]::Escape($Publisher)).
    Replace('$PublisherDisplayName$', [System.Security.SecurityElement]::Escape($PublisherDisplayName)).
    Replace('$Version$', $version).
    Replace('$Language$', $Language)
[System.IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding $false))

$msix = Join-Path $outDir "LCapture_${version}_x64.msix"
& (Join-Path $sdkBin "makeappx.exe") pack /o /d $layout /p $msix
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($TestSign) {
    # The certificate subject must match the manifest Publisher exactly.
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Publisher -and $_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3" -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
            -FriendlyName "LCapture MSIX (teste)" -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    Export-Certificate -Cert $cert -FilePath (Join-Path $outDir "LCapture-teste.cer") | Out-Null

    & (Join-Path $sdkBin "signtool.exe") sign /fd SHA256 /sha1 $cert.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    "Assinado com certificado de teste. Para instalar, o LCapture-teste.cer tem de estar em 'Pessoas Fidedignas' do computador."
}

"Pacote: $msix"
