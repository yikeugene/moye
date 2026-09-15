[CmdletBinding()]
param([switch]$AllowDesktopChanges)

$ErrorActionPreference = 'Stop'
if (-not $AllowDesktopChanges) { throw 'Run this smoke test in a disposable Windows account with -AllowDesktopChanges. It installs and uninstalls Moye and checks desktop shortcuts.' }
$MoyeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$MoyeProject = Get-Content -LiteralPath (Join-Path $MoyeRoot 'src/Moye/Moye.csproj') -Raw
$MoyeVersion = $MoyeProject.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
$MoyeArtifacts = Join-Path $MoyeRoot 'artifacts'
$MoyeInstaller = Join-Path $MoyeArtifacts ('Moye-' + $MoyeVersion + '-Setup-win-x64.exe')
$MoyePayload = Join-Path $MoyeArtifacts 'Moye-win-x64'
$MoyeDefaultInstall = Join-Path $env:LOCALAPPDATA 'Programs/Moye'
$MoyeData = Join-Path $env:LOCALAPPDATA 'Moye'
$MoyeDesktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Moye.lnk'
$MoyeStartShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Moye.lnk'
$MoyeRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\net.yikeugene.moye_is1'
foreach ($MoyeExisting in @($MoyeDefaultInstall, $MoyeData, $MoyeDesktopShortcut, $MoyeStartShortcut, $MoyeRegistry)) {
    if (Test-Path -LiteralPath $MoyeExisting) { throw "Refusing to touch an existing installation, notebook folder or shortcut: $MoyeExisting" }
}
if (-not (Test-Path -LiteralPath $MoyeInstaller -PathType Leaf)) { throw 'Build the installer before running this test.' }
$MoyeExpectedHash = (Get-Content -LiteralPath ($MoyeInstaller + '.sha256')).Split(' ')[0]
if ((Get-FileHash -LiteralPath $MoyeInstaller -Algorithm SHA256).Hash.ToLowerInvariant() -ne $MoyeExpectedHash) { throw 'Installer checksum mismatch.' }
$MoyeSmokeRoot = [IO.Path]::GetFullPath((Join-Path $MoyeArtifacts ('installer-smoke-' + [Guid]::NewGuid().ToString('N'))))
$MoyeArtifactsPrefix = [IO.Path]::GetFullPath($MoyeArtifacts).TrimEnd('\') + '\'
if (-not $MoyeSmokeRoot.StartsWith($MoyeArtifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installer smoke-test path.' }
$MoyeInstallPath = Join-Path $MoyeSmokeRoot 'app'
New-Item -ItemType Directory -Path $MoyeSmokeRoot, $MoyeData -Force | Out-Null
$MoyeSentinel = Join-Path $MoyeData 'installer-preservation-check.txt'
[IO.File]::WriteAllText($MoyeSentinel, 'Synthetic notebook-data preservation marker.')
$MoyeSentinelHash = (Get-FileHash -LiteralPath $MoyeSentinel -Algorithm SHA256).Hash

function Invoke-MoyeSetup([string]$Executable, [string[]]$Arguments) {
    $MoyeSetupProcess = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($MoyeSetupProcess.ExitCode -ne 0) { throw "Installer process failed: $Executable (exit $($MoyeSetupProcess.ExitCode))." }
}
function Assert-MoyeInstalled {
    $MoyeExpectedExe = Join-Path $MoyeInstallPath 'Moye.exe'
    if (-not (Test-Path -LiteralPath $MoyeExpectedExe -PathType Leaf)) { throw 'Moye.exe was not installed.' }
    $MoyeShell = New-Object -ComObject WScript.Shell
    try {
        foreach ($MoyeShortcutPath in @($MoyeDesktopShortcut, $MoyeStartShortcut)) {
            if (-not (Test-Path -LiteralPath $MoyeShortcutPath -PathType Leaf)) { throw "Automatic shortcut missing: $MoyeShortcutPath" }
            $MoyeShortcut = $MoyeShell.CreateShortcut($MoyeShortcutPath)
            try {
                if ($MoyeShortcut.TargetPath -ne $MoyeExpectedExe -or $MoyeShortcut.WorkingDirectory -ne $MoyeInstallPath) { throw 'Shortcut target or working directory is incorrect.' }
            } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($MoyeShortcut) }
        }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($MoyeShell) }
    if ((Get-ItemProperty -LiteralPath $MoyeRegistry).DisplayVersion -ne $MoyeVersion) { throw 'Windows uninstall registration has the wrong version.' }
    foreach ($MoyeSource in Get-ChildItem -LiteralPath $MoyePayload -Recurse -File -Force) {
        $MoyeRelative = [IO.Path]::GetRelativePath($MoyePayload, $MoyeSource.FullName)
        $MoyeInstalled = Join-Path $MoyeInstallPath $MoyeRelative
        if (-not (Test-Path -LiteralPath $MoyeInstalled -PathType Leaf) -or
            (Get-FileHash -LiteralPath $MoyeSource.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $MoyeInstalled -Algorithm SHA256).Hash) { throw "Installed payload mismatch: $MoyeRelative" }
    }
    $MoyeUnexpected = @(Get-ChildItem -LiteralPath $MoyeInstallPath -Recurse -File -Force | Where-Object {
        $MoyeRelative = [IO.Path]::GetRelativePath($MoyeInstallPath, $_.FullName)
        $MoyeRelative -notmatch '^unins\d+\.(exe|dat|msg)$' -and -not (Test-Path -LiteralPath (Join-Path $MoyePayload $MoyeRelative) -PathType Leaf)
    })
    if ($MoyeUnexpected.Count -gt 0) { throw 'Unexpected files in the installed payload.' }
    if ((Get-FileHash -LiteralPath $MoyeSentinel -Algorithm SHA256).Hash -ne $MoyeSentinelHash) { throw 'Installation changed notebook data.' }
}

$MoyeInstalledOnce = $false
try {
    foreach ($MoyePass in @('install', 'reinstall')) {
        Invoke-MoyeSetup $MoyeInstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $MoyeInstallPath + '"'), ('/LOG="' + (Join-Path $MoyeSmokeRoot ($MoyePass + '.log')) + '"'))
        $MoyeInstalledOnce = $true
        Assert-MoyeInstalled
        Write-Host "$MoyePass passed: desktop and Start menu shortcuts, exact payload, uninstall registration, and unchanged notebook data."
    }
    $MoyeUninstaller = Join-Path $MoyeInstallPath 'unins000.exe'
    Invoke-MoyeSetup $MoyeUninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $MoyeSmokeRoot 'uninstall.log') + '"'))
    $MoyeInstalledOnce = $false
    foreach ($MoyeRemoved in @((Join-Path $MoyeInstallPath 'Moye.exe'), $MoyeDesktopShortcut, $MoyeStartShortcut, $MoyeRegistry)) {
        if (Test-Path -LiteralPath $MoyeRemoved) { throw "Uninstall did not remove $MoyeRemoved" }
    }
    if ((Get-FileHash -LiteralPath $MoyeSentinel -Algorithm SHA256).Hash -ne $MoyeSentinelHash) { throw 'Uninstall removed or changed notebook data.' }
    Write-Host 'Uninstall passed: application and shortcuts removed; notebook data preserved.'
    [pscustomobject]@{ version = $MoyeVersion; installerSha256 = $MoyeExpectedHash; install = 'passed'; reinstall = 'passed'; uninstall = 'passed'; desktopShortcut = 'passed'; notebookDataPreserved = $true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $MoyeArtifacts 'installer-smoke-result.json') -Encoding UTF8
}
finally {
    if ($MoyeInstalledOnce -and (Test-Path -LiteralPath (Join-Path $MoyeInstallPath 'unins000.exe'))) {
        Invoke-MoyeSetup (Join-Path $MoyeInstallPath 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
    # Remove only the synthetic file this test created; never recursively delete notebook data.
    if (Test-Path -LiteralPath $MoyeSentinel) { Remove-Item -LiteralPath $MoyeSentinel }
    if ((Test-Path -LiteralPath $MoyeData) -and @(Get-ChildItem -LiteralPath $MoyeData -Force).Count -eq 0) { Remove-Item -LiteralPath $MoyeData }
}
