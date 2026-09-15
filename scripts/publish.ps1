[CmdletBinding()]
param([switch]$NoRestore, [switch]$InstallSdk)

$ErrorActionPreference = 'Stop'
$MoyeEnvironment = & (Join-Path $PSScriptRoot 'build.ps1') -EnvironmentOnly -InstallSdk:$InstallSdk
$MoyeProjectPath = Join-Path $MoyeEnvironment.Root 'src/Moye/Moye.csproj'
[xml]$MoyeProjectXml = Get-Content -LiteralPath $MoyeProjectPath -Raw
$MoyeVersionNode = $MoyeProjectXml.SelectSingleNode('/Project/PropertyGroup/Version')
$MoyeVersion = if ($MoyeVersionNode) { $MoyeVersionNode.InnerText.Trim() } else { '' }
if ($MoyeVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Moye.csproj must contain a release Version such as 1.1.0.' }
$MoyeArtifacts = [IO.Path]::GetFullPath((Join-Path $MoyeEnvironment.Root 'artifacts'))
New-Item -ItemType Directory -Path $MoyeArtifacts -Force | Out-Null
if ((Get-Item -LiteralPath $MoyeArtifacts).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The artifacts directory must not be a symbolic link or junction.' }

function Assert-MoyeArtifactPath([string]$Candidate) {
    $MoyeFullPath = [IO.Path]::GetFullPath($Candidate)
    $MoyePrefix = $MoyeArtifacts.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $MoyeFullPath.StartsWith($MoyePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing an operation outside artifacts: $MoyeFullPath" }
    if (Test-Path -LiteralPath $MoyeFullPath) {
        if ((Get-Item -LiteralPath $MoyeFullPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing a reparse point: $MoyeFullPath" }
    }
    return $MoyeFullPath
}
function Remove-MoyeArtifact([string]$Candidate) {
    $MoyeCheckedPath = Assert-MoyeArtifactPath $Candidate
    if (-not (Test-Path -LiteralPath $MoyeCheckedPath)) { return }
    if ((Get-Item -LiteralPath $MoyeCheckedPath).PSIsContainer) {
        $MoyeLinks = @(Get-ChildItem -LiteralPath $MoyeCheckedPath -Recurse -Force -Attributes ReparsePoint)
        if ($MoyeLinks.Count -gt 0) { throw 'Refusing to recursively remove an artifact containing a symbolic link or junction.' }
        Remove-Item -LiteralPath $MoyeCheckedPath -Recurse -Force
    } else { Remove-Item -LiteralPath $MoyeCheckedPath -Force }
}

$MoyeBuildId = [Guid]::NewGuid().ToString('N')
$MoyeStaging = Assert-MoyeArtifactPath (Join-Path $MoyeArtifacts ('.moye-publish-' + $MoyeBuildId))
$MoyeStagingZip = Assert-MoyeArtifactPath (Join-Path $MoyeArtifacts ('.moye-package-' + $MoyeBuildId + '.zip'))
$MoyeOutput = Assert-MoyeArtifactPath (Join-Path $MoyeArtifacts 'Moye-win-x64')
$MoyePrevious = Assert-MoyeArtifactPath (Join-Path $MoyeArtifacts ('.moye-previous-' + $MoyeBuildId))
$MoyeZip = Assert-MoyeArtifactPath (Join-Path $MoyeArtifacts ('Moye-' + $MoyeVersion + '-win-x64.zip'))
$MoyeMovedPrevious = $false

try {
    $MoyePublishArguments = @('publish', $MoyeProjectPath, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--output', $MoyeStaging, '--nologo', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($NoRestore) { $MoyePublishArguments += '--no-restore' }
    & $MoyeEnvironment.Dotnet @MoyePublishArguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    foreach ($MoyeRequired in @('Moye.exe', 'Moye.dll', 'coreclr.dll', 'PresentationFramework.dll', 'e_sqlite3.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $MoyeStaging $MoyeRequired) -PathType Leaf)) { throw "The self-contained package is missing $MoyeRequired." }
    }
    Copy-Item -LiteralPath (Join-Path $MoyeEnvironment.Root 'README.md') -Destination $MoyeStaging
    Copy-Item -LiteralPath (Join-Path $MoyeEnvironment.Root 'ROADMAP.md') -Destination $MoyeStaging
    Copy-Item -LiteralPath (Join-Path $MoyeEnvironment.Root 'LICENSE') -Destination $MoyeStaging
    # Copy only public user documentation. Internal reports and unrelated files
    # in docs must never be swept into a release by a recursive directory copy.
    $MoyePublicDocs = @(
        'docs/USER_GUIDE.md',
        'docs/FILE_FORMAT.md',
        'docs/THIRD-PARTY-NOTICES.md',
        ('docs/RELEASE_NOTES_' + $MoyeVersion + '.md'),
        'docs/licenses/Apache-2.0.txt',
        'docs/licenses/CSWINRT-LICENSE.txt',
        'docs/licenses/WINDOWS-SDK-LICENSE.txt',
        'docs/licenses/WINDOWS-SDK-NOTICE.md'
    )
    foreach ($MoyePublicDoc in $MoyePublicDocs) {
        $MoyeDocDestination = Join-Path $MoyeStaging $MoyePublicDoc
        New-Item -ItemType Directory -Path (Split-Path -Parent $MoyeDocDestination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $MoyeEnvironment.Root $MoyePublicDoc) -Destination $MoyeDocDestination
    }
    $MoyeNotices = Join-Path $MoyeStaging 'third-party'
    New-Item -ItemType Directory -Path $MoyeNotices -Force | Out-Null
    foreach ($MoyeLicenseFile in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $MoyeLicenseSource = Join-Path $MoyeEnvironment.DotnetRoot $MoyeLicenseFile
        if (-not (Test-Path -LiteralPath $MoyeLicenseSource -PathType Leaf)) { throw "Missing .NET license: $MoyeLicenseSource" }
        Copy-Item -LiteralPath $MoyeLicenseSource -Destination (Join-Path $MoyeNotices ('DOTNET-' + $MoyeLicenseFile))
    }

    # Begin package notice collection. This section only reads metadata and copies notices.
    $MoyeAssets = Get-Content -LiteralPath (Join-Path $MoyeEnvironment.Root 'src/Moye/obj/project.assets.json') -Raw | ConvertFrom-Json
    function Copy-MoyePackageNotice([string]$PackageFolder, [string]$ExpectedId, [string]$ExpectedVersion, [string]$SourceKind) {
        $MoyeNuspec = Get-ChildItem -LiteralPath $PackageFolder -Filter '*.nuspec' -File | Select-Object -First 1
        if (-not $MoyeNuspec) { throw "Missing package metadata: $ExpectedId/$ExpectedVersion" }
        [xml]$MoyePackageXml = Get-Content -LiteralPath $MoyeNuspec.FullName -Raw
        $MoyeMetadata = $MoyePackageXml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        $MoyeId = $MoyeMetadata.SelectSingleNode("*[local-name()='id']").InnerText
        $MoyePackageVersion = $MoyeMetadata.SelectSingleNode("*[local-name()='version']").InnerText
        if ($MoyeId -ne $ExpectedId -or $MoyePackageVersion -ne $ExpectedVersion) { throw "Package metadata does not match restored identity: $ExpectedId/$ExpectedVersion" }
        $MoyePackageDestination = Join-Path $MoyeNotices ($MoyeId + '-' + $MoyePackageVersion)
        New-Item -ItemType Directory -Path $MoyePackageDestination -Force | Out-Null
        Copy-Item -LiteralPath $MoyeNuspec.FullName -Destination $MoyePackageDestination
        Get-ChildItem -LiteralPath $PackageFolder -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD.PARTY.NOTICES)' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $MoyePackageDestination }
        $MoyeLicenseNode = $MoyeMetadata.SelectSingleNode("*[local-name()='license']")
        $MoyeLicenseUrlNode = $MoyeMetadata.SelectSingleNode("*[local-name()='licenseUrl']")
        $MoyeAcceptanceNode = $MoyeMetadata.SelectSingleNode("*[local-name()='requireLicenseAcceptance']")
        $MoyeCopyrightNode = $MoyeMetadata.SelectSingleNode("*[local-name()='copyright']")
        [pscustomobject]@{
            package = $MoyeId + '/' + $MoyePackageVersion
            license = $(if ($MoyeLicenseNode) { $MoyeLicenseNode.InnerText } elseif ($MoyeLicenseUrlNode) { 'See licenseUrl' } else { 'See package metadata' })
            licenseUrl = $(if ($MoyeLicenseUrlNode) { $MoyeLicenseUrlNode.InnerText } else { '' })
            requireLicenseAcceptance = $null -ne $MoyeAcceptanceNode -and $MoyeAcceptanceNode.InnerText -eq 'true'
            copyright = $(if ($MoyeCopyrightNode) { $MoyeCopyrightNode.InnerText } else { '' })
            source = $SourceKind
        }
    }
    $MoyePackageManifest = @()
    $MoyeCollectedPackages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($MoyeLibrary in $MoyeAssets.libraries.PSObject.Properties) {
        if ($MoyeLibrary.Value.type -ne 'package') { continue }
        $MoyeIdentity = $MoyeLibrary.Name.Split('/')
        $MoyePackageFolder = Join-Path $MoyeEnvironment.Nuget $MoyeLibrary.Value.path
        $MoyePackageManifest += Copy-MoyePackageNotice $MoyePackageFolder $MoyeIdentity[0] $MoyeIdentity[1] 'PackageReference'
        [void]$MoyeCollectedPackages.Add($MoyeLibrary.Name)
    }
    # SDK targeting packs are implicit download dependencies and may be absent
    # from assets.libraries. Resolve the exact restored version, never the newest cache folder.
    $MoyeSdkIdentities = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($MoyeFramework in $MoyeAssets.project.frameworks.PSObject.Properties) {
        foreach ($MoyeDownload in $MoyeFramework.Value.downloadDependencies) {
            if ($MoyeDownload.name -notlike 'Microsoft.Windows.SDK.NET.Ref*') { continue }
            if ($MoyeDownload.version -notmatch '^\[([^,\]\s]+)(?:,\s*([^\]\s]+))?\]$') { throw 'The Windows SDK targeting pack must have an exact restored version.' }
            $MoyeSdkVersion = $Matches[1]
            if ($Matches[2] -and $Matches[2] -ne $MoyeSdkVersion) { throw 'The Windows SDK targeting pack version range is not exact.' }
            [void]$MoyeSdkIdentities.Add($MoyeDownload.name + '/' + $MoyeSdkVersion)
        }
    }
    # Published deps provide the resolved identity even when a preinstalled SDK
    # targeting pack did not need a NuGet download on this runner.
    $MoyePublishedDeps = Get-Content -LiteralPath (Join-Path $MoyeStaging 'Moye.deps.json') -Raw | ConvertFrom-Json
    foreach ($MoyeRuntimeLibrary in $MoyePublishedDeps.libraries.PSObject.Properties) {
        if ($MoyeRuntimeLibrary.Name -like 'runtimepack.Microsoft.Windows.SDK.NET.Ref/*') {
            [void]$MoyeSdkIdentities.Add($MoyeRuntimeLibrary.Name.Substring('runtimepack.'.Length))
        }
    }
    $MoyePackageRoots = @($MoyeEnvironment.Nuget) + @($MoyeAssets.packageFolders.PSObject.Properties.Name)
    foreach ($MoyeSdkIdentity in $MoyeSdkIdentities) {
        if (-not $MoyeCollectedPackages.Add($MoyeSdkIdentity)) { continue }
        $MoyeSdkIdentityParts = $MoyeSdkIdentity.Split('/')
        $MoyeSdkCandidates = @($MoyePackageRoots | Select-Object -Unique | ForEach-Object { Join-Path $_ ($MoyeSdkIdentity.ToLowerInvariant()) })
        if ($MoyeEnvironment.DotnetRoot) { $MoyeSdkCandidates += Join-Path $MoyeEnvironment.DotnetRoot ('packs/' + $MoyeSdkIdentity) }
        $MoyeSdkFolder = $MoyeSdkCandidates | Where-Object {
            (Test-Path -LiteralPath $_ -PathType Container) -and @(Get-ChildItem -LiteralPath $_ -Filter '*.nuspec' -File).Count -gt 0
        } | Select-Object -First 1
        if (-not $MoyeSdkFolder) { throw "Missing original targeting-pack metadata for $MoyeSdkIdentity in the restored package folders or SDK packs." }
        $MoyePackageManifest += Copy-MoyePackageNotice $MoyeSdkFolder $MoyeSdkIdentityParts[0] $MoyeSdkIdentityParts[1] 'SdkTargetingPack'
    }
    $MoyeSdkPackages = @($MoyePackageManifest | Where-Object { $_.package -like 'Microsoft.Windows.SDK.NET.Ref/*' })
    if ((Test-Path -LiteralPath (Join-Path $MoyeStaging 'Microsoft.Windows.SDK.NET.dll')) -and $MoyeSdkPackages.Count -eq 0) { throw 'Published Windows SDK projections have no matching targeting-pack notice.' }
    foreach ($MoyeSdkPackage in $MoyeSdkPackages) {
        if (-not $MoyeSdkPackage.licenseUrl) { throw 'The Windows SDK targeting-pack metadata has no license URL.' }
    }
    foreach ($MoyeSdkNotice in @('WINDOWS-SDK-NOTICE.md', 'WINDOWS-SDK-LICENSE.txt', 'CSWINRT-LICENSE.txt')) {
        Copy-Item -LiteralPath (Join-Path $MoyeEnvironment.Root ('docs/licenses/' + $MoyeSdkNotice)) -Destination $MoyeNotices
    }
    $MoyeSdkComponents = @()
    foreach ($MoyeSdkFile in @('Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll')) {
        $MoyeSdkFilePath = Join-Path $MoyeStaging $MoyeSdkFile
        if (-not (Test-Path -LiteralPath $MoyeSdkFilePath -PathType Leaf)) { continue }
        $MoyeSdkVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($MoyeSdkFilePath)
        $MoyeCsWinRt = $MoyeSdkFile -eq 'WinRT.Runtime.dll'
        $MoyeComponentLicenseUrl = if ($MoyeCsWinRt) { 'https://github.com/microsoft/CsWinRT/blob/master/LICENSE' } else { ($MoyeSdkPackages | Select-Object -First 1).licenseUrl }
        if ($MoyeCsWinRt -and $MoyeSdkVersionInfo.ProductVersion -match '\+([a-fA-F0-9]{40})$') {
            $MoyeComponentLicenseUrl = 'https://github.com/microsoft/CsWinRT/blob/' + $Matches[1] + '/LICENSE'
        }
        $MoyeSdkComponents += [pscustomobject]@{
            file = $MoyeSdkFile; fileVersion = $MoyeSdkVersionInfo.FileVersion; productVersion = $MoyeSdkVersionInfo.ProductVersion
            copyright = $MoyeSdkVersionInfo.LegalCopyright; packagedVia = @($MoyeSdkPackages.package)
            license = $(if ($MoyeCsWinRt) { 'MIT' } else { 'Windows SDK license terms' })
            licenseUrl = $MoyeComponentLicenseUrl
            licenseFile = $(if ($MoyeCsWinRt) { 'CSWINRT-LICENSE.txt' } else { 'WINDOWS-SDK-LICENSE.txt' })
        }
    }
    $MoyePackageManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $MoyeNotices 'package-manifest.json') -Encoding UTF8
    $MoyeSdkComponents | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $MoyeNotices 'windows-sdk-components.json') -Encoding UTF8
    # End package notice collection.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($MoyeStaging, $MoyeStagingZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $MoyePackageHash = (Get-FileHash -LiteralPath $MoyeStagingZip -Algorithm SHA256).Hash.ToLowerInvariant()

    if (Test-Path -LiteralPath $MoyeOutput) {
        [void](Assert-MoyeArtifactPath $MoyeOutput)
        [void](Assert-MoyeArtifactPath $MoyePrevious)
        Move-Item -LiteralPath $MoyeOutput -Destination $MoyePrevious
        $MoyeMovedPrevious = $true
    }
    [void](Assert-MoyeArtifactPath $MoyeStaging)
    [void](Assert-MoyeArtifactPath $MoyeOutput)
    Move-Item -LiteralPath $MoyeStaging -Destination $MoyeOutput
    Move-Item -LiteralPath $MoyeStagingZip -Destination $MoyeZip -Force
    Set-Content -LiteralPath ($MoyeZip + '.sha256') -Value ($MoyePackageHash + '  ' + [IO.Path]::GetFileName($MoyeZip)) -Encoding ASCII
    if ($MoyeMovedPrevious) { Remove-MoyeArtifact $MoyePrevious; $MoyeMovedPrevious = $false }
    Write-Host "Portable folder: $MoyeOutput"
    Write-Host "Package: $MoyeZip"
    Write-Host "SHA-256: $MoyePackageHash"
}
catch {
    if ($MoyeMovedPrevious -and -not (Test-Path -LiteralPath $MoyeOutput)) {
        [void](Assert-MoyeArtifactPath $MoyePrevious)
        [void](Assert-MoyeArtifactPath $MoyeOutput)
        Move-Item -LiteralPath $MoyePrevious -Destination $MoyeOutput
        $MoyeMovedPrevious = $false
    }
    throw
}
finally {
    Remove-MoyeArtifact $MoyeStaging
    Remove-MoyeArtifact $MoyeStagingZip
}
