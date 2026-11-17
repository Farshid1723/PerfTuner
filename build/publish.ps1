<#
.SYNOPSIS
  Produces the runnable layout:
    artifacts/PerfTuner/            GUI (PerfTuner.exe)
    artifacts/PerfTuner/Service/    service payload the GUI installs from
    artifacts/cli/                  perftuner-cli.exe
  Framework-dependent (requires the .NET 9 Desktop Runtime).
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$common = @('-c', $Configuration, '-r', $Runtime, '--self-contained', 'false', '--nologo')
dotnet publish (Join-Path $root 'src/PerfTuner.App')     @common -o (Join-Path $out 'PerfTuner')
dotnet publish (Join-Path $root 'src/PerfTuner.Service') @common -o (Join-Path $out 'PerfTuner/Service')
dotnet publish (Join-Path $root 'tools/PerfTuner.Cli')   @common -o (Join-Path $out 'cli')

Write-Host "Published to $out"
