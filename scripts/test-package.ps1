[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)

$ErrorActionPreference = 'Stop'
$MoyeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$MoyePackage = (Resolve-Path -LiteralPath $PackagePath).Path
foreach ($MoyeRequired in @('Moye.exe', 'Moye.dll', 'Moye.deps.json', 'Moye.runtimeconfig.json', 'coreclr.dll', 'PresentationFramework.dll', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'e_sqlite3.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $MoyePackage $MoyeRequired) -PathType Leaf)) { throw "The package is missing $MoyeRequired." }
}
$MoyeCheckRoot = Join-Path $MoyeRoot ('artifacts/package-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $MoyeCheckRoot | Out-Null
$MoyeData = Join-Path $MoyeCheckRoot 'Synthetic library 測試'
$MoyeStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $MoyePackage 'Moye.exe'))
$MoyeStart.UseShellExecute = $false
$MoyeStart.CreateNoWindow = $true
$MoyeStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
# Run outside the application folder and without the build machine's .NET overrides.
$MoyeStart.WorkingDirectory = $MoyeCheckRoot
$MoyeStart.ArgumentList.Add('--check-storage')
$MoyeStart.ArgumentList.Add($MoyeData)
$MoyeStart.RedirectStandardOutput = $true
$MoyeStart.RedirectStandardError = $true
foreach ($MoyeVariable in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE', 'DOTNET_STARTUP_HOOKS')) { [void]$MoyeStart.Environment.Remove($MoyeVariable) }
$MoyeProcess = [Diagnostics.Process]::Start($MoyeStart)
try {
    $MoyeStdout = $MoyeProcess.StandardOutput.ReadToEndAsync()
    $MoyeStderr = $MoyeProcess.StandardError.ReadToEndAsync()
    if (-not $MoyeProcess.WaitForExit(60000)) { $MoyeProcess.Kill($true); $MoyeProcess.WaitForExit(); throw "Packaged storage check timed out. Results: $MoyeCheckRoot" }
    $MoyeStdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $MoyeCheckRoot 'stdout.log')
    $MoyeErrorText = $MoyeStderr.GetAwaiter().GetResult()
    $MoyeErrorText | Set-Content -LiteralPath (Join-Path $MoyeCheckRoot 'stderr.log')
    if ($MoyeProcess.ExitCode -ne 0) { throw "Packaged storage check failed (exit $($MoyeProcess.ExitCode)). $MoyeErrorText`nResults: $MoyeCheckRoot" }
    $MoyeReport = Get-Content -LiteralPath (Join-Path $MoyeData 'storage-check.json') -Raw | ConvertFrom-Json
    if (-not $MoyeReport.success -or -not $MoyeReport.notebookRoundTrip -or -not $MoyeReport.assetRoundTrip -or $MoyeReport.architecture -ne 'X64') { throw 'Packaged storage verification did not complete.' }
    Write-Host "Packaged storage check passed: SQLite $($MoyeReport.sqliteVersion), $($MoyeReport.architecture), notebook/asset save and reopen. Results: $MoyeCheckRoot"
}
finally { $MoyeProcess.Dispose() }
