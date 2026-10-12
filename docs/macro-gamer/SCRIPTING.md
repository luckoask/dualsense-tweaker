# Implemented Lua API

The stable MoonSharp 2.0.0 interpreter runs in `MacroGamer.ScriptHost.exe`, separate from WPF and native output. A script registers physical triggers. The worker returns validated action requests; the parent engine schedules them and owns all synthetic holds. Nothing is emitted until the user enables the engine and starts an enabled script.

```lua
on_press("R1", function()
    key_tap("E", 60)
end)
```

| Function | Actual behavior |
| --- | --- |
| `on_press(buttons, function)` | Registers a button/combo press. `L1+R1` fires when the whole combination becomes pressed. |
| `on_release(buttons, function)` | Registers release of a previously complete combination. |
| `is_pressed(buttons)` | Tests the immutable physical frame associated with this invocation. |
| `input()` | Returns a new Lua table of this frame's approved fields. |
| `variable(name)` | Reads a numeric profile variable, or nil. |
| `key_down(code)`, `key_up(code)` | Requests approved keyboard hold/release. |
| `key_tap(code, milliseconds)` | Requests down, cancelable delay, up. |
| `mouse_down(button)`, `mouse_up(button)`, `mouse_tap(button, milliseconds)` | Requests LEFT/RIGHT/MIDDLE/X1/X2 actions. |
| `mouse_move(x, y)` | Requests relative movement, each coordinate −10000…10000. |
| `mouse_wheel(delta)` | Requests a signed wheel delta, −10000…10000; 120 is the normal one-notch unit. |
| `delay(milliseconds)` | Appends a scheduled delay, 0…60000 ms. |
| `sequence(function)` | Executes a Lua function to append its ordered actions. |
| `log(string)`, `print(string)` | Writes a bounded diagnostic message; one string argument. |
| `cancel()` | Requests emergency stop of parent execution; returned actions are discarded. |

`delay` constructs a sequence; it does not suspend Lua evaluation or fetch a fresh controller frame. Consequently `delay(100); input()` reads the same triggering frame. Lua callback actions execute as one sequence and their holds are released at the end; holds do not persist across callbacks. Script callbacks share their own Lua globals for that script snapshot. Output sequences from queued script callbacks execute in order; ordinary macros may run concurrently under the shared ownership ledger.

`input()` fields: `connected`, `left_x`, `left_y`, `right_x`, `right_y` (0…255, center approximately 128), `l2`, `r2` (0…255), `buttons` (documented PadButtons bitmask), `touch1_active`, `touch1_x`, `touch1_y`, `touch2_active`, `touch2_x`, `touch2_y`. Digital names come directly from [ControllerContracts.cs](../../src/DualSenseTweaker.ControllerBridge/ControllerContracts.cs), including standard and Edge-specific buttons. Use `is_pressed` instead of relying on numeric bit positions.

Keyboard codes: ASCII letters/digits, F1…F24, SPACE, ENTER, TAB, ESCAPE, BACKSPACE, DELETE, INSERT, HOME, END, PAGEUP, PAGEDOWN, UP, DOWN, LEFT, RIGHT, CAPSLOCK, LSHIFT/RSHIFT, LCTRL/RCTRL, LALT/RALT; SHIFT/CTRL/ALT select the left modifier. Codes are case insensitive. Keyboard-layout interpretation belongs to the receiving Windows application.

Limits: 64000 source characters; four active script snapshots; 64 handlers per script; 64 pending invocations; 128000 Lua instructions per request; 2048 actions; 128 diagnostic strings of 2048 characters; 60-second sequence budget; two-second worker request deadline; 128 MiB managed heap; 256 MiB Windows Job process memory and one-process limit. Workers are killed when the parent closes their Job handle. The engine limits eight concurrent runs, 100000 actions/run, 1000 repeats and maximum 60-second runtime, and spaces output operations by at least 4 ms. Timing is best effort under Windows scheduling; lateness above 15 ms is logged.

No file, network, process, reflection, native-code or unrestricted CLR API is exposed. `os`, `io`, `package`, `require`, `load`, filesystem loaders, `debug`, `coroutine`, `clr`, `System` and related globals are removed. No framework userdata is registered. The process boundary also handles native-library callbacks that cannot be interrupted by Lua instruction yielding. Syntax validation executes initialization in a fresh worker and discards actions. Initialization may append actions when the actual script is started.

The sample library in [samples/macro-gamer](../../samples/macro-gamer) contains R1 tap, combo sequence, release, conditional mouse and cancellation examples. Automated tests execute these files against the same production interpreter and worker.
