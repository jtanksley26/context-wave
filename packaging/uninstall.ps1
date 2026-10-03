# Removes MD Reader for the current user: the Claude registration, the shortcut and the app
# folder. Voices and settings are kept unless you ask for them to be removed.
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\MdReader'
$data = Join-Path $env:LOCALAPPDATA 'MdReader'
$bridge = Join-Path $target 'bridge\MdReader.Bridge.exe'

Get-Process MdReader.App -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $bridge) {
    Write-Host "Removing MD Reader from Claude..."
    & $bridge setup --remove
}

Get-Process MdReader.Bridge -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MD Reader.lnk'
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }

if (Test-Path $target) {
    Set-Location $env:TEMP
    Remove-Item $target -Recurse -Force
    Write-Host "Removed $target"
}

if (Test-Path $data) {
    $answer = Read-Host "Also delete your voices, settings and logs in $data? [y/N]"
    if ($answer -match '^\s*y') {
        Remove-Item $data -Recurse -Force
        Write-Host "Removed $data"
    }
}

Write-Host "MD Reader has been removed."
