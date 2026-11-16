# Creates a Desktop shortcut to the dev build (%LOCALAPPDATA%\LCaptureDev\LCapture.exe), with the
# app's own icon. Run tools\run-dev.ps1 at least once first so that exe exists.
# Usage: powershell -File tools\create-desktop-shortcut.ps1
$ErrorActionPreference = "Stop"

$target = Join-Path $env:LOCALAPPDATA "LCaptureDev\LCapture.exe"
if (-not (Test-Path $target)) {
    throw "Não encontrei $target — corre primeiro tools\run-dev.ps1 para gerar o build."
}

$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "LCapture.lnk"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = Split-Path $target
$shortcut.IconLocation = "$target,0"
$shortcut.Description = "LCapture (dev)"
$shortcut.Save()

"Atalho criado: $shortcutPath -> $target"
