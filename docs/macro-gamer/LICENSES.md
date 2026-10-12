# Dependency review

| Dependency | Version | License / distribution |
| --- | --- | --- |
| MoonSharp | NuGet 2.0.0 (assembly package version 2.0.0.0) | BSD-3-Clause; original copyright and KopiLua notices are preserved in `third-party/MoonSharp-LICENSE.txt` and copied into the companion payload |
| HidSharp, existing core dependency | 2.6.4 | Apache-2.0; checked the package's `LICENSE.txt`, retained in `third-party/HidSharp-LICENSE.txt` |
| NAudio.Wasapi / NAudio.Core, existing core dependencies | 3.1.0 | MIT according to the exact NuGet metadata and [upstream](https://github.com/naudio/NAudio/blob/main/LICENSE); retained notice in `third-party/NAudio-LICENSE.txt` |
| .NET / WPF private runtime | 10.0.12, SDK 10.0.401 | .NET runtime distribution and third-party notices accompany self-contained publish output; [runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| WiX SDK, UI and Util extensions | 5.0.2 | Existing MS-RL build dependencies, retained at v5; no upgrade to the v6+ fee-bearing distribution. [Versioned source license](https://github.com/wixtoolset/wix/blob/v5.0.2/LICENSE.TXT) |
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT, build/test only |
| xUnit / Visual Studio runner | 2.9.3 / 4.0.1 | Apache-2.0, build/test only |
| actions/checkout, setup-dotnet, upload-artifact | Pinned commits recorded in workflow | MIT upstream repositories, CI only; never application payload |

MoonSharp is the only added third-party application interpreter. The companion, IPC, output, and presentation projects otherwise use .NET platform libraries and project references. No virtual gamepad driver or externally downloaded native executable is installed. Existing controller-model asset attribution remains in the root README. This work does not change the repository's product license or relicense existing assets.

The standalone worker is intentionally self-contained in `MacroGamer/script-worker`, because Windows Desktop's `WindowsBase.dll` differs from the console-runtime facade. This costs another private runtime in the optional payload and avoids an unsafe mixed-runtime installation. Core-only installations do not include either companion runtime.
