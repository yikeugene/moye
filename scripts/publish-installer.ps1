[CmdletBinding()]
param([switch]$NoRestore, [switch]$InstallSdk, [switch]$InstallCompiler)

$ErrorActionPreference = 'Stop'
$MoyeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$MoyeToolsFolder = Join-Path $MoyeRoot '.tools'
New-Item -ItemType Directory -Path $MoyeToolsFolder -Force | Out-Null
$MoyeToolsInfo = Get-Item -LiteralPath $MoyeToolsFolder
if ($MoyeToolsInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    # Resolve a shared worktree cache before invoking Setup; do not disable its
    # protections against traversing untrusted junctions during installation.
    $MoyeToolsFolder = $MoyeToolsInfo.ResolveLinkTarget($true).FullName
}
$MoyeCompilerFolder = Join-Path $MoyeToolsFolder 'inno-7.1.0'
$MoyeCompiler = Join-Path $MoyeCompilerFolder 'ISCC.exe'

if (-not (Test-Path -LiteralPath $MoyeCompiler -PathType Leaf)) {
    if (-not $InstallCompiler) { throw 'Inno Setup 7.1.0 is required. Run scripts/publish-installer.ps1 -InstallCompiler to install the pinned compiler under .tools.' }
    $MoyeCompilerInstaller = Join-Path $MoyeToolsFolder 'innosetup-7.1.0-x64.exe'
    New-Item -ItemType Directory -Path (Split-Path -Parent $MoyeCompilerInstaller) -Force | Out-Null
    $MoyeCompilerHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
    if (-not (Test-Path -LiteralPath $MoyeCompilerInstaller -PathType Leaf)) {
        Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile $MoyeCompilerInstaller
    }
    if ((Get-FileHash -LiteralPath $MoyeCompilerInstaller -Algorithm SHA256).Hash.ToLowerInvariant() -ne $MoyeCompilerHash) {
        throw 'The Inno Setup compiler download does not match its pinned SHA-256. It was not executed.'
    }
    $MoyeCompilerInstallLog = Join-Path $MoyeToolsFolder 'inno-compiler-install.log'
    $MoyeCompilerArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/NOICONS', ('/DIR="' + $MoyeCompilerFolder + '"'), ('/LOG="' + $MoyeCompilerInstallLog + '"'))
    $MoyeCompilerProcess = Start-Process -FilePath $MoyeCompilerInstaller -ArgumentList $MoyeCompilerArguments -WindowStyle Hidden -Wait -PassThru
    if ($MoyeCompilerProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $MoyeCompiler -PathType Leaf)) { throw "Inno Setup compiler installation failed (exit $($MoyeCompilerProcess.ExitCode)); see $MoyeCompilerInstallLog." }
}
$MoyeCompilerVersion = & $MoyeCompiler '--version'
if ($LASTEXITCODE -ne 0 -or ($MoyeCompilerVersion -join '').Trim() -ne '7.1.0') { throw 'The compiler must be Inno Setup 7.1.0.' }

& (Join-Path $PSScriptRoot 'publish.ps1') -NoRestore:$NoRestore -InstallSdk:$InstallSdk
[xml]$MoyeProject = Get-Content -LiteralPath (Join-Path $MoyeRoot 'src/Moye/Moye.csproj') -Raw
$MoyeVersion = $MoyeProject.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($MoyeVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid application version.' }
$MoyeArtifacts = Join-Path $MoyeRoot 'artifacts'
$MoyePayload = Join-Path $MoyeArtifacts 'Moye-win-x64'
Copy-Item -LiteralPath (Join-Path $MoyeCompilerFolder 'license.txt') -Destination (Join-Path $MoyePayload 'third-party/INNO-SETUP-LICENSE.txt')
$MoyePayloadFiles = @(Get-ChildItem -LiteralPath $MoyePayload -File -Recurse -Force)
$MoyeForbidden = '(?i)(^|/)(AGENTS?\.md|TEST_REPORT\.md|writing-preferences[^/]*)$|(^|/)(\.git|\.codex|\.agents|\.tools|artifacts|sample-library|qa-library|TestResults)(/|$)|ui-preview|\.(db|db3|db-wal|db-shm|db-journal|sqlite|sqlite3|moye|pfx|p12|key|pem|log|trx|pdb|cs|xaml|ps1|bundle)$|(^|/)\.env'
if (@(Get-ChildItem -LiteralPath $MoyePayload -Force -Recurse -Attributes ReparsePoint).Count -gt 0) { throw 'Installer payload must not contain symbolic links or junctions.' }
foreach ($MoyeFile in $MoyePayloadFiles) {
    $MoyeRelativePath = [IO.Path]::GetRelativePath($MoyePayload, $MoyeFile.FullName).Replace('\', '/')
    if ($MoyeRelativePath -match $MoyeForbidden) { throw "Local-only file in installer payload: $MoyeRelativePath" }
}
$MoyeProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $MoyePayload 'Moye.dll')).ProductVersion
if ($MoyeProductVersion.Split('+')[0] -ne $MoyeVersion) { throw 'Published application version does not match the project.' }

& $MoyeCompiler '--quiet-progress' ('--define=MoyeVersion=' + $MoyeVersion) ('--define=MoyePayload=' + $MoyePayload) ('--define=MoyeOutput=' + $MoyeArtifacts) (Join-Path $MoyeRoot 'installer/Moye.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE." }
$MoyeInstaller = Join-Path $MoyeArtifacts ('Moye-' + $MoyeVersion + '-Setup-win-x64.exe')
if (-not (Test-Path -LiteralPath $MoyeInstaller -PathType Leaf)) { throw 'Installer compiler produced no EXE.' }
$MoyeInstallerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($MoyeInstaller).FileVersion.Trim()
if ($MoyeInstallerVersion -ne ($MoyeVersion + '.0')) { throw "Unexpected installer version: $MoyeInstallerVersion" }
$MoyeInstallerHash = (Get-FileHash -LiteralPath $MoyeInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($MoyeInstaller + '.sha256') -Value ($MoyeInstallerHash + '  ' + [IO.Path]::GetFileName($MoyeInstaller)) -Encoding ASCII
Write-Host "Installer: $MoyeInstaller"
Write-Host "SHA-256: $MoyeInstallerHash"
