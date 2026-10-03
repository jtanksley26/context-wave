# Installs MD Reader for the current user: copies the app, the bundled voice and default
# settings, adds a Start Menu shortcut, and optionally connects it to Claude.
param(
    # Skip the question and do not register with Claude.
    [switch]$NoClaude,
    # Skip the question and register with Claude.
    [switch]$Claude
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'app'
$target = Join-Path $env:LOCALAPPDATA 'Programs\MdReader'
$data = Join-Path $env:LOCALAPPDATA 'MdReader'

if (-not (Test-Path (Join-Path $source 'MdReader.App.exe'))) {
    throw "The 'app' folder is missing. Extract the whole zip before running Install.cmd."
}

Write-Host "Installing MD Reader to $target"

# A running copy keeps its files locked.
Get-Process MdReader.App, MdReader.Bridge -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $target | Out-Null
robocopy $source $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the app failed (robocopy exit code $LASTEXITCODE)." }

# Files that came from a downloaded zip are marked as blocked, which stops libraries loading.
Get-ChildItem $target -Recurse -File | Unblock-File

# The bundled voice saves a download, which a work network may not allow.
$voices = Join-Path $PSScriptRoot 'voices'
if (Test-Path $voices) {
    foreach ($voice in Get-ChildItem $voices -Directory) {
        $destination = Join-Path $data "models\$($voice.Name)"
        if (Test-Path $destination) {
            Write-Host "Voice $($voice.Name) is already installed."
        }
        else {
            New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
            Copy-Item $voice.FullName $destination -Recurse
            Write-Host "Installed voice $($voice.Name)."
        }
    }
}

# Settings are only supplied on a first install; existing ones are kept.
$settings = Join-Path $data 'settings.json'
$defaults = Join-Path $PSScriptRoot 'default-settings.json'
if ((Test-Path $defaults) -and -not (Test-Path $settings)) {
    New-Item -ItemType Directory -Force $data | Out-Null
    Copy-Item $defaults $settings
    Write-Host "Installed default settings."
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MD Reader.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $target 'MdReader.App.exe'
$link.WorkingDirectory = $target
$link.Description = 'Reads markdown aloud'
$link.Save()
Write-Host "Added a Start Menu shortcut: MD Reader"

# The app shows its pages in the Edge WebView2 runtime, which Windows 11 includes.
$webView = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$hasWebView = @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                "HKLM:\$webView", "HKCU:\$webView") | Where-Object { Test-Path $_ }
if (-not $hasWebView) {
    Write-Warning "The Microsoft Edge WebView2 Runtime was not found. MD Reader needs it:"
    Write-Warning "https://developer.microsoft.com/microsoft-edge/webview2/"
}

$connect = $Claude
if (-not $Claude -and -not $NoClaude) {
    Write-Host ""
    Write-Host "MD Reader can register itself with Claude Code and the Claude desktop app on this PC."
    Write-Host "That adds the md-reader tools and the hook that passes Claude's replies to MD Reader."
    $answer = Read-Host "Connect MD Reader to Claude now? [Y/n]"
    $connect = $answer -notmatch '^\s*n'
}
if ($connect) {
    Write-Host ""
    & (Join-Path $target 'bridge\MdReader.Bridge.exe') setup
    Write-Host ""
    Write-Host "Restart Claude for the connection to take effect."
}
else {
    Write-Host "Skipped. To connect later, run: `"$target\bridge\MdReader.Bridge.exe`" setup"
}

Write-Host ""
Write-Host "MD Reader is installed. Start it from the Start Menu."
Write-Host "To remove it, run Uninstall.cmd in $target"
