# Macro Gamer 1.1.0 — engineering and verification report

## Source and delivery

- Original main commit: `922c2fd409bd3d0d8a520a430bafc9ee0b02635d` (1.0.7).
- Tested implementation and MSI source commit: `1a35b378f9dc9433ec25a43236ff50a9bac7c594`.
- Branch: [`feat/macro-gamer`](https://github.com/luckoask/dualsense-tweaker/tree/feat/macro-gamer).
- Product version: 1.1.0. UpgradeCode remains `97574113-B7C6-41D8-854C-AA1042ECCF7A`.
- The final documentation commit is recorded in the delivered `publish/DELIVERY-REPORT.md` and `publish/delivery-manifest.json`; it does not change the tested application source.
- Local MSI: `publish/DualSenseTweaker-1.1.0-x64.msi`, 110800228 bytes.
- MSI SHA-256: `292995b17500afca7f31e3613077c50ccfcac89070fe4a95b575819156e7ede3`.
- [Successful Windows workflow](https://github.com/luckoask/dualsense-tweaker/actions/runs/38238359200), [MSI and evidence artifact](https://github.com/luckoask/dualsense-tweaker/actions/runs/38238359200/artifacts/11742230309).

The repository was initially private and reconstructed through the authorized connector, including verification of the original tree and binary hashes. Later Git fetch became available. CLI push still lacked credentials, so the authorized GitHub contents/Git-data API published the coherent milestone history. Each published tree was checked against the tested local tree. Local milestone histories remain in archive branches; no user work was reset or discarded. Main was fetched again before delivery and remains at the original SHA. No changes were pushed to main and nothing was merged.

Creating a pull request returned GitHub integration error 403, “Resource not accessible by integration.” The branch is published and [PULL-REQUEST.md](PULL-REQUEST.md) contains the prepared description. [Open the branch comparison](https://github.com/luckoask/dualsense-tweaker/compare/main...feat/macro-gamer?expand=1) to create the PR using an account with permission.

## Implemented product

Macro Gamer is its own WPF executable and eight-section interface: dashboard, controllers, macros, ordered editor/recorder, scripts, profiles, diagnostics and settings. It uses the established theme palettes and controls, SK/EN/CZ resources, persistent dark/light mode, keyboard navigation and per-monitor DPI declaration. Controls invoke actual commands. The existing Tweaker interface was not redesigned or combined with it.

Twelve solution projects now include a shared controller bridge, portable macro core, output, scripting, isolated script host, portable presentation, separate WPF companion, original tests and two companion test projects. Explicit application composition supplies services and Windows dialogs/process helpers. The original application references only the small bridge contract/transport, not the companion engine or interpreter.

The existing HidSharp parser and manager remain the single HID owner. An additional identity-bearing event feeds a bounded publisher away from the HID reader; the original selected-controller event remains intact. The companion's pipe reader runs off the UI thread. Read-only current-user pipes carry validated, bounded JSON envelopes, monotonic timestamps, source sessions and physical connection generations. Queue gaps, heartbeat loss, disconnect and identity changes stop execution and require explicit rearming.

The asynchronous engine implements physical button transitions, press/release/held/combo/threshold/connection triggers, timed keyboard/mouse sequences, repeat/hold/toggle behavior, cancelable scheduling, runtime/action/concurrency bounds and diagnostic lateness. Synthetic outputs do not feed physical triggers. A serialized ownership ledger reference-counts overlapping holds, canonicalizes modifier/key aliases, releases owners in cleanup and retains failed releases for explicit retry. Stop All and the registered global Ctrl+Shift+F10 shortcut disarm execution. Every launch starts disabled.

The diagnostic backend records real execution events. The Windows backend constructs ABI-correct SendInput packets and checks actual insertion results. Virtual-controller output is explicitly unavailable; no driver, HID filter, service, controller concealment, game-process injection or console authentication code is installed.

MoonSharp runs real Lua in a separate worker with a restricted API, no unrestricted OS/CLR userdata, instruction/action/log/queue limits, a two-second request deadline, a 128 MiB managed heap and a 256 MiB Windows Job process limit. The parent alone emits and owns output. Validation runs the production interpreter in a fresh worker and discards actions. Running scripts retain immutable source/input/variable snapshots while the editor changes. Five shipped Lua examples are executed by tests.

Profiles provide create/rename/duplicate/delete/import/export, controller and game association, numeric variables, macro/script enable flags, backend selection and persisted recording mappings. JSON is versioned, bounded and atomically replaced. Corrupt data is rejected and preserved for diagnosis. Invalid unfinished editor values are saved as a separate draft and restored with the engine disabled. Recording captures meaningful digital/analog/touch changes; explicit digital mappings convert traces into playable keyboard macros. Three shipped JSON macro presets import and round-trip through production storage.

The same WiX v5 MSI has required `Application`, default-off `MacroGamer`, and optional desktop shortcuts. Sibling payloads prevent companion assemblies from entering the required wildcard. The optional console worker retains its own private runtime because its WindowsBase facade differs from WPF. Runtime and library notices accompany the payloads. Silent-install conditions protect the required core and reject orphan companion shortcuts. Upgrade identity, core launch option, graceful-close actions and user-data retention remain intact.

## Exact verification results

Initial host: Linux Mint, .NET SDK 10.0.401/runtime 10.0.12. Unadapted baseline restore returned NETSDK1100. With Windows targeting enabled, the original solution restored and built in Release with zero warnings/errors. Original WPF tests could not execute on Linux because Microsoft.WindowsDesktop.App is unavailable there. These outcomes are retained in `artifacts/validation/baseline-*.log`.

Final Windows environment: clean GitHub-hosted Windows Server 2025 x64, .NET SDK 10.0.401/runtime 10.0.12. The workflow ran:

```powershell
dotnet restore DualSenseTweaker.sln
dotnet build DualSenseTweaker.sln -c Release --no-restore
dotnet test DualSenseTweaker.sln -c Release --no-build -m:1 --logger trx --results-directory artifacts/validation/windows
./tools/build-installer.ps1
```

| Check | Actual result |
| --- | --- |
| Restore and Release solution build | Passed; zero warnings/errors |
| Existing Tweaker regression suite | 240 passed, zero failed/skipped |
| Portable companion suite | 117 passed, zero failed/skipped; also passed on Linux |
| Native WPF/desktop suite | 1 passed, zero failed/skipped |
| All tests | **358 passed**, zero failed/skipped |
| WPF rendering | 48 PNGs: all eight pages × SK/EN/CZ × dark/light; selected values and field layout checked |
| Native keyboard/mouse | F24 down/up received by own foreground HWND; X2 down/up and wheel received |
| Global emergency stop | Real registered Ctrl+Shift+F10 received WM_HOTKEY and released a held F24 |
| Actual self-contained MSI build | Passed; zero warnings/errors; 105.7 MiB |
| Previous-version package | Original 1.0.7 worktree built into a real MSI |
| MSI table/payload checks | Required level 1, independent optional level 2, INSTALLLEVEL 1, no companion files in core |
| Clean default installation | Core present; companion files, shortcut and registry flag absent |
| Explicit optional feature installation | Companion/worker files and installation flag present; no automatic launch |
| Installed Lua worker | Real production interpreter validated the sample request |
| Installed WPF executable | Main window opened; separate `--exit` invocation closed it gracefully |
| Repair | Deleted MacroGamer.exe restored; installed companion launched again |
| Optional removal | Companion absent; core retained; user data preserved |
| 1.0.7 → 1.1.0 major upgrade | Passed; optional feature remained absent until explicitly selected |
| Full uninstall | Executables removed; AppData marker preserved |

The portable suite includes corrupted profiles/drafts, imports/exports, trigger transitions, identity/session changes, overlap ownership, cancellation/races, timing, held/toggle/repetition/resource bounds, real named-pipe framing/overflow, script sandbox restrictions, syntax locations, shipped examples, instruction exhaustion, isolated OOM, native-callback hangs, worker timeout cleanup and recording persistence/deletion. Hostile-script fixtures and test modules run sequentially in CI; concurrency tests retain their explicit parallel tasks.

Exact TRX files, MSI verbose logs, feature/file tables, installed-worker output, installed-UI confirmation, native-output confirmation and screenshots are under `artifacts/validation/windows-final`. The final artifact archive was downloaded and its files extracted locally; MSI bytes were hashed after extraction. Intermediate failures led to runtime isolation, correct MSI condition prefixes, COM-result suppression, stable translated choices and waiting for the actual titled WPF window rather than a temporary untitled window. The final run passes every gate.

## Install and test

Core only, with the optional companion absent:

```powershell
msiexec /i DualSenseTweaker-1.1.0-x64.msi /qn /norestart
```

Both applications, with optional desktop shortcuts:

```powershell
msiexec /i DualSenseTweaker-1.1.0-x64.msi /qn ADDLOCAL=Application,Desktop,MacroGamer,MacroDesktop
```

Launch `C:\Program Files\LCKS\DualSense Tweaker\MacroGamer\MacroGamer.exe`. Open Tweaker from the Controllers page, connect a physical controller, enable the R1 → E sample, save and arm with diagnostic output. Press R1 and inspect Down/Up. Then select keyboard/mouse, save/rearm, focus a supported receiving application such as Notepad and press R1 to emit E. Stop using Ctrl+Shift+F10 or Stop All. Recording/playback, maintenance commands, Lua API and troubleshooting are documented in [USER-GUIDE.md](USER-GUIDE.md) and [SCRIPTING.md](SCRIPTING.md).

## Limits and next verification

Physical DualSense/Edge devices over USB/Bluetooth were not available and were not claimed as hardware-tested. Windows 11 was not the CI image; the native APIs and clean installation were verified on Server 2025. The next acceptance work is a controlled Windows 11 hardware matrix covering all four device/transport combinations, reconnect, simultaneous identities, exclusive HID/Steam Input interaction and scaling on actual monitors.

Keyboard/mouse acceptance is verified in the dedicated test window. Arbitrary games, protected input stacks, physical modifier interference and higher-integrity targets are not guaranteed; SendInput and normal physical controller input can coexist. Negative cross-account/elevation pipe tests were not performed; the platform restriction is configured at both ends and same-user transport was exercised.

Virtual/XInput output is unavailable because no maintained, signed, licensed redistribution solution was verified. ViGEmBus is retired; VirtualPad requires separate commercial verification. Analog/touch traces are retained but not replayed as a virtual gamepad. Native PS5 and Remote Play output are outside scope. There is no syntax highlighter or alternate configurable emergency shortcut in this release. Same-account multi-session use is not supported. Profiles have a 4 MiB file bound and recordings/action counts have documented resource limits.

Normal close, stop, disconnect and worker failure release owned inputs. Forced parent termination/power loss cannot execute cleanup; no external input-release watchdog is supplied. The MSI/executables are unsigned, as in the existing project. Further engineering should prioritize hardware validation, release signing with the owner's certificate, an independently evaluated watchdog, negative-user IPC fixtures and measured latency/scaling on actual Windows 11 gaming setups.
