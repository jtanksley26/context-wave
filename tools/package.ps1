# Builds the install package: dist\ContextWave-<version>-win-x64.zip
#
#   pwsh tools\package.ps1
#   pwsh tools\package.ps1 -Version 1.2 -Voice kokoro-en-v0_19
#   pwsh tools\package.ps1 -Voice ''          # no bundled voice; it downloads on first use
param(
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd'),
    # The id of an installed voice to bundle, from %LOCALAPPDATA%\MdReader\models.
    [string]$Voice = 'vits-piper-en_US-lessac-medium'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
$name = "ContextWave-$Version-win-x64"
$stage = Join-Path $root "dist\$name"
$zip = "$stage.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force "$stage\app" | Out-Null

# Self-contained, so the target PC needs no .NET runtime. Each build goes to its own folder
# so it neither touches a running copy in out\ nor mixes with the development build.
# The bridge gets a folder of its own in the package: it and the app depend on different
# versions of the same libraries, and two self-contained programs in one folder overwrite
# each other's copies.
$outputs = [ordered]@{ 'MdReader.App' = "$stage\app"; 'MdReader.Bridge' = "$stage\app\bridge" }
foreach ($project in $outputs.Keys) {
    dotnet publish "$root\src\$project" -c Release -r win-x64 --self-contained true `
        "-p:MdReaderOut=../../out-pkg/$project/" -p:DebugType=none -o $outputs[$project] -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

# Publishing the app leaves the bridge's launcher beside it without the bridge's libraries
# (the app only references the bridge for build order). It cannot run from there.
Remove-Item (Join-Path $stage 'app\MdReader.Bridge.*') -Force

Copy-Item "$root\packaging\Install.cmd", "$root\packaging\install.ps1", "$root\packaging\README.txt" $stage
Copy-Item "$root\packaging\Uninstall.cmd", "$root\packaging\uninstall.ps1" "$stage\app"

# First-run settings: the bundled voice, and replies off so nothing speaks unexpectedly.
$settings = [ordered]@{ Replies = 'off' }
if ($Voice) {
    $source = Join-Path $env:LOCALAPPDATA "MdReader\models\$Voice"
    if (-not (Test-Path $source)) { throw "Voice '$Voice' is not installed at $source." }
    New-Item -ItemType Directory -Force "$stage\voices" | Out-Null
    Copy-Item $source "$stage\voices\$Voice" -Recurse
    $settings.ModelId = $Voice
    $settings.SpeakerId = 0
}
$settings | ConvertTo-Json | Set-Content "$stage\default-settings.json" -Encoding utf8

Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal

$size = '{0:N0} MB' -f ((Get-Item $zip).Length / 1MB)
Write-Host "Created $zip ($size)"
