[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoRestore,
    [switch]$InstallSdk,
    [switch]$EnvironmentOnly
)

$ErrorActionPreference = 'Stop'
$MoyeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$MoyeTools = Join-Path $MoyeRoot '.tools'
$MoyeCliCache = Join-Path $MoyeTools 'cli'
$MoyeNugetCache = Join-Path $MoyeTools 'nuget'
New-Item -ItemType Directory -Path $MoyeCliCache, $MoyeNugetCache -Force | Out-Null
$env:DOTNET_CLI_HOME = $MoyeCliCache
$env:NUGET_PACKAGES = $MoyeNugetCache
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$MoyeDotnet = Join-Path $MoyeTools 'dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $MoyeDotnet -PathType Leaf)) {
    $MoyeSystemDotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($MoyeSystemDotnet) { $MoyeDotnet = $MoyeSystemDotnet.Source }
}
$MoyeHasSdk = $false
if (Test-Path -LiteralPath $MoyeDotnet -PathType Leaf) {
    $MoyeSdkList = & $MoyeDotnet --list-sdks
    $MoyeHasSdk = @($MoyeSdkList | Where-Object { $_ -match '^10\.' }).Count -gt 0
}
if (-not $MoyeHasSdk) {
    if (-not $InstallSdk) { throw 'A .NET 10 SDK is required. Run scripts/build.ps1 -InstallSdk to install it inside .tools.' }
    $MoyeInstallerSource = [Uri]'https://dot.net/v1/dotnet-install.ps1'
    if ($MoyeInstallerSource.Scheme -ne 'https' -or $MoyeInstallerSource.DnsSafeHost -ne 'dot.net') { throw 'Unexpected SDK installer source.' }
    $MoyeInstaller = Join-Path $MoyeTools 'dotnet-install.ps1'
    Invoke-WebRequest -Uri $MoyeInstallerSource.AbsoluteUri -OutFile $MoyeInstaller -UseBasicParsing
    & $MoyeInstaller -Channel '10.0' -InstallDir (Join-Path $MoyeTools 'dotnet') -NoPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The .NET SDK installer failed.' }
    $MoyeDotnet = Join-Path $MoyeTools 'dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $MoyeDotnet -PathType Leaf)) { throw 'SDK installation did not produce dotnet.exe.' }
}
$env:DOTNET_ROOT = Split-Path -Parent $MoyeDotnet
$MoyeEnvironment = [pscustomobject]@{ Root = $MoyeRoot; Dotnet = $MoyeDotnet; DotnetRoot = $env:DOTNET_ROOT; Nuget = $MoyeNugetCache }
if ($EnvironmentOnly) { return $MoyeEnvironment }

$MoyeBuildArguments = @('build', (Join-Path $MoyeRoot 'src/Moye/Moye.csproj'), '--configuration', $Configuration, '--nologo')
if ($NoRestore) { $MoyeBuildArguments += '--no-restore' }
& $MoyeDotnet @MoyeBuildArguments
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
