[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoRestore,
    [switch]$NoBuild,
    [switch]$InstallSdk,
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$MoyeEnvironment = & (Join-Path $PSScriptRoot 'build.ps1') -EnvironmentOnly -InstallSdk:$InstallSdk
$MoyeResults = Join-Path $MoyeEnvironment.Root 'artifacts/TestResults'
$MoyeTestArguments = @('test', (Join-Path $MoyeEnvironment.Root 'tests/Moye.Tests/Moye.Tests.csproj'), '--configuration', $Configuration, '--nologo', '--results-directory', $MoyeResults, '--logger', 'trx;LogFileName=Moye.Tests.trx')
if ($NoRestore) { $MoyeTestArguments += '--no-restore' }
if ($NoBuild) { $MoyeTestArguments += '--no-build' }
if ($Filter) { $MoyeTestArguments += @('--filter', $Filter) }
& $MoyeEnvironment.Dotnet @MoyeTestArguments
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
