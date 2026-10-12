# Add optional standalone Macro Gamer with Lua macros and selectable MSI feature

Adds `MacroGamer.exe` as a separate Windows companion with its own SK/EN/CZ interface, settings, profiles, graphical editor, physical input recorder, sandboxed Lua editor and diagnostics. The existing Tweaker remains the sole HID owner and retains its UI; a restricted, bounded, identity-aware named pipe supplies normalized input to the companion.

The cancelable macro engine schedules approved keyboard/mouse actions using monotonic time and reference-counts held outputs across runs. Windows SendInput and diagnostic backends are implemented. Lua runs in quota-limited workers supervised by the parent; virtual controller output explicitly reports its unavailability and installs no drivers. The engine starts disabled and stops on source loss, profile changes, errors, the global emergency hotkey and normal closure.

The same WiX v5 MSI retains the product UpgradeCode and required `Application` feature, with an independent `MacroGamer` feature off by default. Separate payloads keep all companion files absent from core-only installation. The optional self-contained console worker has its own runtime directory.

Validation evidence and limitations are recorded in [ENGINEERING-REPORT.md](ENGINEERING-REPORT.md). Windows CI builds the real MSI, runs the original and new suites, renders all pages in SK/EN/CZ dark/light modes, verifies native input and global stop, and exercises clean installation, feature repair/removal, upgrade from 1.0.7 and uninstall. Physical USB/Bluetooth DualSense/Edge hardware testing remains a separate acceptance step. No console/Remote Play output or game-specific anti-cheat compatibility is claimed.
