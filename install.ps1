param([string]$FanControlDir = 'C:\Program Files (x86)\FanControl')
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
$dll = Join-Path $PSScriptRoot 'FanControl.DRGB.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Extract the compiled release ZIP and run its install.ps1.' }
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $manifest.PluginSha256) {
        throw 'Plugin SHA256 does not match the package manifest.'
    }
}
if (!(Test-Path -LiteralPath (Join-Path $FanControlDir 'FanControl.exe'))) { throw 'FanControl.exe was not found in FanControlDir.' }
$runtime = Get-Content -LiteralPath (Join-Path $FanControlDir 'FanControl.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.tfm -ne 'net10.0') { throw 'This release targets FanControl on .NET 10 (tested against V283).' }
$running = @(Get-Process -Name FanControl,DRGB -ErrorAction SilentlyContinue)
if ($running.Count) { throw 'Exit FanControl and DRGB before installing; this script does not stop applications.' }
foreach ($name in @('FanControl.Plugins.dll', 'HidSharp.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $FanControlDir $name))) { throw "FanControl dependency missing: $name" }
}
$plugins = Join-Path $FanControlDir 'Plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
$destination = Join-Path $plugins 'FanControl.DRGB.dll'
if (Test-Path -LiteralPath $destination) {
    $backup = $destination + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.bak'
    Copy-Item -LiteralPath $destination -Destination $backup
    Write-Output "Previous plugin backed up: $backup"
}
Copy-Item -LiteralPath $dll -Destination $destination -Force
Unblock-File -LiteralPath $destination
Write-Output "Installed: $destination"
Write-Output 'Start FanControl, enable DeepRGB V5F in Plugins, and refresh sensors. Keep DRGB closed while using this controller.'
