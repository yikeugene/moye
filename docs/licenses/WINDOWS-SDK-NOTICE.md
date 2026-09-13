# Windows SDK and C#/WinRT notices

Moye's Windows release includes `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` supplied by the Windows SDK targeting pack. These components retain their upstream licenses; Moye's MIT license applies to Moye's original source code.

## Windows SDK targeting pack

The reviewed build restored `Microsoft.Windows.SDK.NET.Ref` version `10.0.26100.57`. Its original NuGet metadata identifies Microsoft as the copyright holder, sets `requireLicenseAcceptance` to `true`, and provides [Microsoft's Windows SDK license URL](https://aka.ms/WinSDKLicenseURL). It does not declare an MIT license expression.

[WINDOWS-SDK-LICENSE.txt](WINDOWS-SDK-LICENSE.txt) is an unmodified copy of the SDK terms published in [Microsoft's win32metadata repository](https://github.com/microsoft/win32metadata/blob/main/licenses/sdk_license.txt), retrieved on 2026-09-13. Its original identifier is `EULAID:WIN10SDK.RTM.AUG_2018_en-US`. The text, including its original spelling and French provisions, has been preserved.

SHA-256: `2789763155a8ab04143396c7d1af037d73782378c256bed308aad7a25d6850a2`.

The reviewed `Microsoft.Windows.SDK.NET.dll` has file version `10.0.26100.55` and product version `10.0.26100.55+7e1ba99b2f0c1ea5311dcb3acfe20a75335efd6e`. Package and assembly versions are recorded separately because they need not be identical.

## C#/WinRT runtime

The reviewed `WinRT.Runtime.dll` has file version `2.2.0.48161` and product version `2.2.0.48161+8649ee3eeb2445ca2a36d80d878ef60b96a6c65d`.

[CSWINRT-LICENSE.txt](CSWINRT-LICENSE.txt) is an unmodified copy of Microsoft's MIT license from the [C#/WinRT source revision identified by that binary](https://github.com/microsoft/CsWinRT/blob/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/LICENSE), retrieved on 2026-09-13. It retains the original Microsoft copyright notice and permission text.

SHA-256: `9906940f61b1f0b533fa7d99baf55178b2808fbe113ea51dfbfad8572ccd5f2b`.

## Release evidence

The release's `third-party/package-manifest.json` records actual restored package versions, license expressions or URLs, copyright notices, and acceptance metadata. The targeting pack identity is read from `project.assets.json` and the published `Moye.deps.json`, including implicit download dependencies and resolved runtime packs. The collector checks the configured package folders and SDK packs for that exact version; it does not choose the newest local cache folder. The original targeting-pack `.nuspec` is also included.

`third-party/windows-sdk-components.json` records the actual published SDK and WinRT DLL versions and their license sources. This notice describes the reviewed baseline; the manifests identify the files in a particular release. The original license text remains authoritative.

For the separate .NET and WPF runtime components, see the release's `third-party/DOTNET-LICENSE.txt`, `third-party/DOTNET-ThirdPartyNotices.txt`, and Microsoft's [.NET Windows license information](https://github.com/dotnet/core/blob/main/license-information-windows.md).
