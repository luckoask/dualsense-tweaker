# Capability and verification matrix

Primary target is Windows 11 x64. Hosted Windows CI uses Windows Server 2025; it verifies Windows APIs and installation on that clean image, not every Windows 11 build or physical device.

| Controller / connection | Input route | Automated coverage | Physical hardware verification |
| --- | --- | --- | --- |
| DualSense USB | Existing HidSharp parser → Tweaker → restricted pipe | Existing report/model tests + simulated normalized frames | Not performed |
| DualSense Bluetooth | Existing Bluetooth parser → same bridge | Existing report/protocol tests + simulated normalized frames | Not performed |
| DualSense Edge USB | Existing Edge report parser, additional buttons → same bridge | Existing model/protocol tests + simulated identities | Not performed |
| DualSense Edge Bluetooth | Existing Bluetooth/Edge handling → same bridge | Existing model/protocol tests + simulated identities | Not performed |

| Capability | Delivered behavior | Limit |
| --- | --- | --- |
| Read controller | Reuses Tweaker's actual device connection, buttons, sticks, triggers, touch and available battery | Requires Tweaker and accessible HID device; hardware matrix awaits real devices |
| Execute Lua | Real isolated MoonSharp workers and approved parent action pipeline | Explicit arming/start and resource quotas |
| Emit keyboard/mouse | Real Windows SendInput backend | Receiving app's focus, keyboard layout, input stack, integrity and game rules determine acceptance |
| Emit virtual controller | Explicitly unavailable selector/backend | No verified maintained, signed, licensed driver bundled |
| Replace native controller input | Not implemented | Physical controller remains visible and unchanged by the companion |
| Native PS5 / separate console | Unsupported | Windows software does not alter reports sent to a separate console |
| Remote Play integration | Outside this release | No independently tested integration |

Steam Input may provide its own controller-to-keyboard mapping while Macro Gamer emits an additional key. Configure mappings explicitly to avoid duplicate actions. Native DualSense games can continue receiving physical input simultaneously; this release does not suppress it. Exclusively claimed devices or externally installed HID filters can make the existing Tweaker connection unavailable. The companion never opens a second competing HID connection and installs no filters, virtual buses, services or drivers.

The named-pipe protocol restricts both ends to the current Windows user. On Windows the runtime also checks elevation level; start both applications normally. Different simultaneous login sessions for the same account are not a supported multi-session setup. There is no network listener or authenticated/unauthenticated remote-control endpoint.

ViGEmBus was [retired](https://docs.nefarius.at/projects/ViGEm/End-of-Life/). [VirtualPad](https://docs.nefarius.at/projects/VirtualPad/) requires a commercial agreement and separate signing/redistribution verification. Neither is bundled. A future backend requires verified driver distribution, explicit reversible installation and separately tested physical/virtual input coexistence.

[Microsoft documents SendInput's UIPI restriction](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput): input can be blocked across integrity levels, and its error does not always identify UIPI. A successful API return means insertion into Windows input processing; it does not prove acceptance by an arbitrary game. Tests use a dedicated foreground test window and check received messages, not only mocked packet construction. Game-process injection, concealment and anti-cheat bypass are absent.

Use only where synthetic inputs comply with the game's rules. No claim is made that a particular protected game accepts this output.
