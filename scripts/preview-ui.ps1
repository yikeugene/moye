[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$MoyeEnvironment = & (Join-Path $PSScriptRoot 'build.ps1') -EnvironmentOnly
$MoyePreviewProject = Join-Path $MoyeEnvironment.Root 'tools/Moye.UiPreview/Moye.UiPreview.csproj'
if (-not $NoRestore) {
    # Reuse the existing SDK/package cache; this tool needs no new dependencies.
    & $MoyeEnvironment.Dotnet restore $MoyePreviewProject --source $MoyeEnvironment.Nuget -p:NuGetAudit=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Local-cache restore failed. Build the main project first to populate its dependencies.' }
}
& $MoyeEnvironment.Dotnet build $MoyePreviewProject --configuration $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw "Preview build failed with exit code $LASTEXITCODE." }
$MoyePreviewAssembly = Join-Path $MoyeEnvironment.Root "tools/Moye.UiPreview/bin/$Configuration/net10.0-windows10.0.26100.0/Moye.UiPreview.dll"
# Executes an STA console renderer. It never calls Window.Show or Application.Run.
& $MoyeEnvironment.Dotnet $MoyePreviewAssembly $MoyeEnvironment.Root
if ($LASTEXITCODE -ne 0) { throw "Offscreen preview failed with exit code $LASTEXITCODE." }
