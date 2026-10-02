# Builds Nexus for local development. Closes a running Nexus first (it locks Nexus.exe).
# Usage: tools/build.ps1 [-Configuration Debug|Release] [-Test] [-Run [-RunArgs "--page=games"]]
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [switch]$Test,
    [switch]$Run,
    [string]$RunArgs = ''
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

Get-Process Nexus -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

& $dotnet build (Join-Path $root 'src\Nexus.App\Nexus.App.csproj') -c $Configuration -p:Platform=x64 -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Test) {
    & $dotnet test --project (Join-Path $root 'tests\Nexus.Core.Tests\Nexus.Core.Tests.csproj') -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Run) {
    $exe = Join-Path $root "src\Nexus.App\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\Nexus.exe"
    if ($RunArgs) { Start-Process $exe -ArgumentList $RunArgs } else { Start-Process $exe }
}
