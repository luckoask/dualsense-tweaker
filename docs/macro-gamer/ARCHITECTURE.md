# Macro Gamer architecture and source audit

## Baseline

Base: `main` commit `922c2fd409bd3d0d8a520a430bafc9ee0b02635d` (DualSense Tweaker 1.0.7). The private repository was fetched through the authorized GitHub connector because local Git has no credentials. All 131 blobs, the complete Git tree, and the original root commit were reconstructed and verified against Git object hashes; `git fsck --full` passed. The original GLB was retrieved unchanged from its attributed public upstream because the connector cannot decode that binary; its blob hash is identical. The initial worktree was clean. Work continues on `feat/macro-gamer`. No AGENTS.md or repository-specific agent instructions exist in this revision.

The solution contains Core, the WPF executable, and a Windows/WPF xUnit test project. Core targets `net10.0-windows`, uses HidSharp 2.6.4 and NAudio.Wasapi 3.1.0. Explicit service composition lives in `App.OnStartup`; view models receive `AppServices`. Localization uses Slovak source text with English/Czech translations and dynamic WPF resources. Theme dictionaries define the LCKS blue/purple/orange palette, typography and rounded controls. Existing profile JSON and application settings live under `%APPDATA%\DualSenseTweaker` and are outside companion persistence.

`DualSenseManager` enumerates Sony USB/Bluetooth devices and opens each controlled identity through `DualSenseDevice`. Selection affects its existing `InputReceived` event only. Controller MAC identities survive USB/Bluetooth changes when available; otherwise the HID path is the identity. Each device has dedicated reader/writer threads; input events execute synchronously on the reader thread at approximately 250 Hz. Reports already contain standard/Edge digital buttons, analog sticks/triggers, touch contacts, IMU and status. Device timestamps are Stopwatch elapsed values local to a connection and restart on reconnect. The manager publishes device snapshots and selected connection changes. Disposal restores physical controller outputs and stops the HID threads.

The MSI uses WiX 5.0.2, `WixUI_FeatureTree`, required `Application`, optional desktop shortcut, per-machine x64 installation, and UpgradeCode `97574113-B7C6-41D8-854C-AA1042ECCF7A`. `tools/build-installer.ps1` publishes self-contained files and harvests a recursive core payload. New payload directories must be siblings so this wildcard cannot harvest companion files. Existing graceful-close actions and user-data retention must be preserved.

On the Linux development host, unmodified `dotnet restore` reports NETSDK1100. With `-p:EnableWindowsTargeting=true`, baseline restore and Release build pass with zero warnings/errors. The original test project requires Microsoft.WindowsDesktop.App and cannot run on the Linux runtime. Exact logs are in `artifacts/validation/baseline-*.log`; Windows CI must run the original suite and MSI build. Hardware and clean Windows MSI tests are separate acceptance gates, not inferred from compilation.

## Module boundaries

| Project | Responsibilities |
| --- | --- |
| ControllerBridge | Versioned normalized controller contracts, bounded framing, current-user named pipes. Shared by the existing application and companion, without macro/output dependencies. |
| MacroGamer.Core | Pure managed transitions, validated macro definitions, monotonic scheduling, cancellation/state machine, output ownership, recording, diagnostics and versioned atomic profile storage. No WPF/HID/native dependencies. |
| MacroGamer.Output | Windows keyboard/mouse SendInput and explicit unavailable virtual-gamepad status. Diagnostic output is in Core for hardware-independent tests. |
| MacroGamer.Scripting | Restricted MoonSharp API and process supervision; immutable editor/run snapshots; no arbitrary CLR object exposure. |
| MacroGamer.ScriptHost | Separate bounded worker for Lua evaluation. It can request approved actions only; the parent owns synthetic inputs and releases them on worker failure. |
| MacroGamer | Independently launched WPF executable, service composition, view models, editor, profiles, settings, controller status and diagnostics. |

## Input ownership and IPC

The existing Tweaker process remains the sole HID owner. Launch Tweaker explicitly to supply real input to Macro Gamer; the companion reports source offline while Tweaker is not running. The companion never opens another HID connection, changes physical haptics, installs drivers or launches itself from the core installer. A new identity-bearing manager event preserves existing selected-input semantics. Its subscriber performs only a bounded enqueue; serialization, named-pipe writes and consumers run away from the HID reader.

The transport is local named pipes with `PipeOptions.CurrentUserOnly` at both ends, a versioned length-prefixed JSON envelope, bounded lengths/queues and a server session identifier. Frames carry controller identities and global Stopwatch timestamps; a reconnect starts a new input generation and disarms execution. A dropped frame/queue overflow is observable and must stop execution rather than lose a release transition silently. Status/heartbeat timeouts stop all synthetic input. Different Windows users and different elevation levels are rejected by the platform pipe check. There is no network listener and no remote game-control API.

## Execution and output

The engine starts disabled on every launch. Explicit arming, profile activation and controller selection define the input generation. Press, release, combination, held and trigger-threshold transitions match physical frames only; synthetic output never feeds trigger matching. Definitions are validated and snapshotted before execution. Limits bound action counts, concurrency, repeat counts, action rate and elapsed runtime. Scheduling is based on monotonic elapsed time with cancelable asynchronous waits; UI timers are for presentation only.

Every run receives an output owner identifier. A serialized ownership ledger reference-counts overlapping synthetic holds, releases each owner in `finally`, and preserves failed releases for diagnostics/retry. Stop All, emergency hotkey, profile/backend switch, disconnect, lost heartbeat and close disarm and cancel runs before releasing outputs. States are Idle, Armed, Running, Stopping, Stopped and Error. A backend failure is reported; no successful input is fabricated.

SendInput supports keyboard/mouse actions, not controller/XInput replacement. It cannot guarantee that arbitrary games accept events, and UIPI prevents injection into higher-integrity applications. Physical inputs still reach games normally, so Steam Input/native controller bindings can combine with synthetic keyboard/mouse actions. There is no controller hiding, process injection or anti-cheat bypass.

## Scripting decision and limits

The stable MoonSharp 2.0.0 NuGet package (BSD-3-Clause) executes successfully on .NET 10. Seven dependency probes passed. Its hard sandbox removes OS, IO, loading, debug and dynamic modules, but leaves a package global; denied globals must also be explicitly cleared. No automatic userdata registration or framework types are exposed. AutoYieldCounter supports Lua instruction slices, but native library callbacks and memory allocation require an additional process boundary and parent timeout/Windows Job memory limits. These constraints must be tested against the production interpreter, including infinite loops and actual shipped examples.

API direction: `on_press`, `on_release`, `is_pressed`, `input`, `key_down`, `key_up`, `key_tap`, mouse actions, `delay`, `sequence`, `cancel`, `variable`, and `log`. The final user guide will list only implemented functions. Script registration and output are disabled until explicit arming; validation cannot emit real input. Editing creates a new text snapshot and cannot mutate a running interpreter.

## Persistence and installation

Companion profiles/settings/macros/scripts use `%APPDATA%\DualSenseTweaker\MacroGamer`, versioned JSON, bounded parsing and atomic replacement. Imports validate schemas, enum values, sizes and resource limits, use GUID file identities and never interpret names as paths. Defaults produce no automatic input.

The existing required `Application` MSI feature remains. A sibling `MacroGamer` feature uses Level 2 with INSTALLLEVEL 1 so it is absent by default and can be selected in the same wizard or through ADDLOCAL. Its executable, dependencies, script host, registration and Start Menu shortcut belong only to its own feature. UpgradeCode stays unchanged. Application version must advance for a real major upgrade; same-version upgrade is not a substitute for correct product versioning. User data remain outside MSI-owned folders.

## Primary references

- [MoonSharp sandbox](https://www.moonsharp.org/sandbox.html), [instruction yielding and caveats](https://www.moonsharp.org/coroutines.html), [stable package](https://www.nuget.org/packages/MoonSharp/2.0.0), [license](https://github.com/xanathar/moonsharp/blob/master/LICENSE).
- [CurrentUserOnly pipe authorization](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0).
- [SendInput return values and UIPI limits](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
- [ViGEmBus retirement](https://docs.nefarius.at/projects/ViGEm/End-of-Life/). No retired driver is bundled.
- [VirtualPad commercial partnership requirement](https://docs.nefarius.at/projects/VirtualPad/). Redistribution/signing rights are not established, so virtual controller output remains unavailable unless separately verified.
