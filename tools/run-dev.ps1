# Builds LCapture and runs it from a local folder (%LOCALAPPDATA%\LCaptureDev). Windows' camera
# privacy does not let apps on network paths (e.g. \\wsl.localhost\...) open the capture card,
# so a build inside WSL must be copied to the PC before running.
# Usage: powershell -File tools\run-dev.ps1
$ErrorActionPreference = "Stop"
# No Resolve-Path: on \\wsl.localhost it returns a provider path that dotnet cannot open.
$project = Join-Path $PSScriptRoot "..\src\LCapture\LCapture.csproj"
$target = Join-Path $env:LOCALAPPDATA "LCaptureDev"

Get-Process LCapture -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$target*" } | Stop-Process -Force
Start-Sleep -Milliseconds 500

dotnet build $project -c Debug -p:Platform=x64 -o $target
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Start-Process (Join-Path $target "LCapture.exe")
"A correr: $target\LCapture.exe  (registo: $env:LOCALAPPDATA\LCapture\lcapture.log)"
