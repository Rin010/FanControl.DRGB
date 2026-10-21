param(
    [string]$FanControlDir = 'C:\Program Files (x86)\FanControl',
    [string]$DotNet = ''
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DRGB_TEST_HOST_DIR = $FanControlDir
if (!$DotNet) {
    $candidate = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($candidate -and (& $candidate.Source --list-sdks)) { $DotNet = $candidate.Source }
    else {
        $localSdk = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\dotnet\dotnet.exe'
        if (Test-Path -LiteralPath $localSdk) { $DotNet = $localSdk }
        else { throw 'A .NET 10 SDK is required. Supply -DotNet with the SDK dotnet.exe path.' }
    }
}
Push-Location $PSScriptRoot
try {
    [xml]$projectXml = Get-Content -LiteralPath 'src\FanControl.DRGB\FanControl.DRGB.csproj'
    $version = [string]$projectXml.Project.PropertyGroup.Version
    & $DotNet build 'src\FanControl.DRGB\FanControl.DRGB.csproj' -c Release --nologo "-p:FanControlDir=$FanControlDir"
    if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed.' }
    & $DotNet run --project 'tests\FanControl.DRGB.Tests\FanControl.DRGB.Tests.csproj' -c Release "-p:FanControlDir=$FanControlDir"
    if ($LASTEXITCODE -ne 0) { throw 'Offline tests failed.' }
    & $DotNet build 'tools\Drgb.Diagnostics\Drgb.Diagnostics.csproj' -c Release --nologo "-p:FanControlDir=$FanControlDir"
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostics compilation failed.' }
    $dist = Join-Path $PSScriptRoot 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    Copy-Item -LiteralPath 'src\FanControl.DRGB\bin\Release\net10.0\FanControl.DRGB.dll' -Destination $dist -Force
    foreach ($name in @('README.md', 'README.zh-CN.md', 'VALIDATION.zh-CN.md', 'LICENSE', 'install.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $dist -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $dist 'docs') -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'docs') -File -Filter '*.md' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dist 'docs' $_.Name) -Force
    }
    $binary = Join-Path $dist 'FanControl.DRGB.dll'
    $manifest = [ordered]@{
        Version = $version
        TargetFramework = 'net10.0'
        FanControlApiSha256 = (Get-FileHash -LiteralPath (Join-Path $FanControlDir 'FanControl.Plugins.dll') -Algorithm SHA256).Hash
        HidSharpSha256 = (Get-FileHash -LiteralPath (Join-Path $FanControlDir 'HidSharp.dll') -Algorithm SHA256).Hash
        PluginSha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'manifest.json') -Encoding utf8
    $packageFiles = @('FanControl.DRGB.dll', 'README.md', 'README.zh-CN.md', 'VALIDATION.zh-CN.md', 'LICENSE', 'install.ps1', 'manifest.json', 'docs') |
        ForEach-Object { Join-Path $dist $_ }
    Compress-Archive -LiteralPath $packageFiles -DestinationPath (Join-Path $dist "FanControl.DRGB-$version.zip") -Force
    $sourceStage = Join-Path $PSScriptRoot ('artifacts\source-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
    # Package only reviewed source categories. Gitignored local files, captures,
    # credentials, dependency binaries and build output must not enter source ZIPs.
    $sourceFiles = @(
        foreach ($name in @('.gitignore', '.gitattributes', 'README.md', 'README.zh-CN.md', 'VALIDATION.zh-CN.md',
            'LICENSE', 'build.ps1', 'install.ps1', 'Directory.Build.props')) {
            Get-Item -LiteralPath (Join-Path $PSScriptRoot $name)
        }
        foreach ($name in @('src', 'tests', 'tools', 'docs')) {
            Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $name) -Recurse -File |
                Where-Object { $_.Extension -in @('.cs', '.csproj', '.md') -and
                    $_.FullName -notmatch '[\\/](bin|obj|artifacts|dist)[\\/]' }
        }
    )
    foreach ($sourceFile in $sourceFiles) {
        $relative = [System.IO.Path]::GetRelativePath($PSScriptRoot, $sourceFile.FullName)
        $target = Join-Path $sourceStage $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $target
    }
    Compress-Archive -Path (Join-Path $sourceStage '*') -DestinationPath (Join-Path $dist "FanControl.DRGB-$version-source.zip") -Force
    Write-Output "Built and tested: $dist\FanControl.DRGB-$version.zip"
}
finally { Pop-Location }
