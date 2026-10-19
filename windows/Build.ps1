param([string]$Runtime = 'win-x64', [string]$OutputDirectory = '', [string]$InnoCompiler = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/windows' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
function Invoke-Dotnet([string[]]$Arguments) { & dotnet.exe @Arguments; if ($LASTEXITCODE -ne 0) { throw 'dotnet failed' } }
Push-Location $repo
try {
    Invoke-Dotnet -Arguments @('build', 'Spottarr.slnx', '-c', 'Release')
    Invoke-Dotnet -Arguments @('run', '--project', 'tests/Spottarr.Windows.Tests', '-c', 'Release', "-p:SolutionDir=$repo/", '--', $repo)
    Invoke-Dotnet -Arguments @('artifacts/bin/Spottarr.Tests/release/Spottarr.Tests.dll', '--no-ansi', '--progress', 'off')
    $bundle = Join-Path $OutputDirectory 'Spottarr-Windows'
    # Publish into a fresh directory: stale files must not silently enter a release.
    if (Test-Path $bundle) { throw 'Output bundle already exists. Choose a fresh output directory.' }
    Invoke-Dotnet -Arguments @('publish', 'src/Spottarr.Windows/Spottarr.Windows.csproj', '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', "-p:SolutionDir=$repo/", '-o', $bundle)
    Invoke-Dotnet -Arguments @('publish', 'src/Spottarr.Web/Spottarr.Web.csproj', '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', "-p:SolutionDir=$repo/", '-o', (Join-Path $bundle 'server'))
    Copy-Item (Join-Path $repo 'LICENSE') $bundle
    Copy-Item (Join-Path $repo 'docs/windows-setup.md') (Join-Path $bundle 'WINDOWS-README.md')
    & (Join-Path $PSScriptRoot 'Smoke.ps1') -ServerDirectory (Join-Path $bundle 'server')
    Compress-Archive -Path $bundle -DestinationPath (Join-Path $OutputDirectory 'Spottarr-Windows-win-x64-preview.zip')
    if ($InnoCompiler) {
        & $InnoCompiler "/DBundleDir=$bundle" "/O$OutputDirectory" (Join-Path $PSScriptRoot 'Spottarr.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    }
    Get-ChildItem $OutputDirectory -File | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $([IO.Path]::GetFileName($_.Path))" } | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt')
} finally { Pop-Location }
