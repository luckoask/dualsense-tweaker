using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;
using MoonSharp.Interpreter;

namespace DualSenseTweaker.MacroGamer.Scripting;

/// <summary>Restricted interpreter. Production callers must use ScriptWorkerClient, not evaluate untrusted code in the UI process.</summary>
public sealed class LuaSandbox
{
    private readonly Script _script;
    private readonly List<(ScriptBinding Binding, DynValue Function)> _handlers = [];
    private readonly List<MacroAction> _actions = [];
    private readonly List<string> _logs = [];
    private IReadOnlyDictionary<string, double> _variables = new Dictionary<string, double>();
    private PadFrame? _input;
    private bool _cancel;
    private int _slices;
    private int _scheduledMs;
    private bool _initialized;
    private string _sourceName = "script.lua";
    public LuaSandbox()
    {
        _script = new(CoreModules.Preset_HardSandbox);
        foreach (string global in new[] { "os", "io", "file", "package", "require", "load", "loadfile", "loadsafe", "loadfilesafe", "dofile", "debug", "dynamic", "coroutine", "collectgarbage", "luanet", "clr", "System" })
            _script.Globals[global] = DynValue.Nil;
        // Potentially large/blocking library calls remain protected by the worker process limits.
        Register("on_press", args => Bind(args, TriggerKind.Press));
        Register("on_release", args => Bind(args, TriggerKind.Release));
        Register("key_down", args => AddControl(args, ActionKind.KeyDown));
        Register("key_up", args => AddControl(args, ActionKind.KeyUp));
        Register("key_tap", args => AddControl(args, ActionKind.KeyTap));
        Register("mouse_down", args => AddControl(args, ActionKind.MouseDown));
        Register("mouse_up", args => AddControl(args, ActionKind.MouseUp));
        Register("mouse_tap", args => AddControl(args, ActionKind.MouseTap));
        Register("mouse_move", args => { Count(args, 2); Add(new() { Kind = ActionKind.MouseMove, X = Integer(args[0], -10_000, 10_000), Y = Integer(args[1], -10_000, 10_000) }); return DynValue.Nil; });
        Register("mouse_wheel", args => { Count(args, 1); Add(new() { Kind = ActionKind.MouseWheel, Y = Integer(args[0], -10_000, 10_000) }); return DynValue.Nil; });
        Register("delay", args => { Count(args, 1); Add(new() { Kind = ActionKind.Delay, DurationMs = Integer(args[0], 0, 60_000) }); return DynValue.Nil; });
        Register("is_pressed", args => { Count(args, 1); return DynValue.NewBoolean(_input?.IsPressed(Buttons(args[0])) == true); });
        Register("input", args => { Count(args, 0); return InputTable(); });
        Register("variable", args => { Count(args, 1); string key = Text(args[0], 64); return _variables.TryGetValue(key, out double value) ? DynValue.NewNumber(value) : DynValue.Nil; });
        Register("log", args => { Count(args, 1); if (_logs.Count >= 128) throw new ScriptRuntimeException("Log quota exceeded."); _logs.Add(Text(args[0], 2048)); return DynValue.Nil; });
        Register("print", args => { Count(args, 1); if (_logs.Count >= 128) throw new ScriptRuntimeException("Log quota exceeded."); _logs.Add(Text(args[0], 2048)); return DynValue.Nil; });
        Register("cancel", args => { Count(args, 0); _cancel = true; return DynValue.Nil; });
        Register("sequence", args => { Count(args, 1); if (args[0].Type != DataType.Function) throw new ScriptRuntimeException("sequence expects a Lua function."); return DynValue.NewTailCallReq(args[0]); });
    }
    public ScriptResponse Process(ScriptRequest request)
    {
        _actions.Clear(); _logs.Clear(); _cancel = false; _slices = 0; _scheduledMs = 0;
        try
        {
            if (!Enum.IsDefined(request.Operation) || request.Source is null || request.Source.Length > 64_000 ||
                request.Name is null || request.Name.Length > 128 || request.Variables is null || request.Variables.Count > 64 ||
                request.Variables.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 64 || !double.IsFinite(p.Value)))
                throw new InvalidDataException("Invalid script request or variables.");
            request.Input?.Validate(); _input = request.Input; _variables = new Dictionary<string, double>(request.Variables);
            if (request.Operation is ScriptOperation.Load or ScriptOperation.Validate)
            {
                if (_initialized) throw new InvalidOperationException("Use a fresh worker for a new script snapshot.");
                _initialized = true;
                _sourceName = request.Name;
                Execute(_script.LoadString(request.Source, null, request.Name));
            }
            else
            {
                if (request.HandlerIndex < 0 || request.HandlerIndex >= _handlers.Count) throw new InvalidDataException("Invalid script handler.");
                Execute(_handlers[request.HandlerIndex].Function);
            }
            return new() { Success = true, Actions = _actions.ToArray(), Logs = _logs.ToArray(), Bindings = _handlers.Select(h => h.Binding).ToArray(), CancelRequested = _cancel, InstructionSlices = _slices };
        }
        catch (InterpreterException ex) { return new() { Error = ex.DecoratedMessage ?? $"{_sourceName}: {ex.Message}", Logs = _logs.ToArray(), InstructionSlices = _slices }; }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or OverflowException)
        { return new() { Error = ex.Message, Logs = _logs.ToArray(), InstructionSlices = _slices }; }
    }
    private void Execute(DynValue function)
    {
        var coroutine = _script.CreateCoroutine(function).Coroutine; coroutine.AutoYieldCounter = 1000;
        do
        {
            if (++_slices > 128) throw new ScriptRuntimeException("Lua instruction quota exceeded (128,000 instructions per request).");
            coroutine.Resume();
        } while (coroutine.State != CoroutineState.Dead);
    }
    private void Register(string name, Func<CallbackArguments, DynValue> function) => _script.Globals[name] = DynValue.NewCallback((_, args) => function(args));
    private DynValue Bind(CallbackArguments args, TriggerKind kind)
    {
        Count(args, 2); if (_handlers.Count >= 64) throw new ScriptRuntimeException("Trigger registration quota exceeded.");
        if (args[1].Type != DataType.Function) throw new ScriptRuntimeException("Trigger callback must be a Lua function.");
        _handlers.Add((new(_handlers.Count, kind, Buttons(args[0])), args[1])); return DynValue.Nil;
    }
    private DynValue AddControl(CallbackArguments args, ActionKind kind)
    {
        bool tap = kind is ActionKind.KeyTap or ActionKind.MouseTap;
        Count(args, tap ? 2 : 1);
        Add(new() { Kind = kind, Code = Text(args[0], 16), DurationMs = tap ? Integer(args[1], 1, 60_000) : 0 }); return DynValue.Nil;
    }
    private void Add(MacroAction action)
    {
        action.Validate(); if (_actions.Count >= 2048) throw new ScriptRuntimeException("Action quota exceeded.");
        _scheduledMs = checked(_scheduledMs + (action.Kind is ActionKind.Delay or ActionKind.KeyTap or ActionKind.MouseTap ? action.DurationMs : 4));
        if (_scheduledMs > 60_000) throw new ScriptRuntimeException("Sequence duration quota exceeded.");
        _actions.Add(action);
    }
    private DynValue InputTable()
    {
        var table = new Table(_script); var analog = _input?.Analog ?? PadAnalog.Centered;
        table["connected"] = _input is not null;
        table["left_x"] = (double)analog.LeftX; table["left_y"] = (double)analog.LeftY; table["right_x"] = (double)analog.RightX; table["right_y"] = (double)analog.RightY;
        table["l2"] = (double)analog.L2; table["r2"] = (double)analog.R2; table["buttons"] = (double)(uint)(_input?.Buttons ?? PadButtons.None);
        table["touch1_active"] = _input?.First.Active == true; table["touch1_x"] = (double)(_input?.First.X ?? 0); table["touch1_y"] = (double)(_input?.First.Y ?? 0);
        table["touch2_active"] = _input?.Second.Active == true; table["touch2_x"] = (double)(_input?.Second.X ?? 0); table["touch2_y"] = (double)(_input?.Second.Y ?? 0);
        return DynValue.NewTable(table);
    }
    private static PadButtons Buttons(DynValue value)
    {
        string text = Text(value, 128); var buttons = PadButtons.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<PadButtons>(part, true, out var button) || button == PadButtons.None || !Enum.IsDefined(button))
                throw new ScriptRuntimeException($"Unknown controller button: {part}");
            buttons |= button;
        }
        return buttons;
    }
    private static void Count(CallbackArguments args, int count) { if (args.Count != count) throw new ScriptRuntimeException($"Expected {count} arguments."); }
    private static string Text(DynValue value, int max)
    { if (value.Type != DataType.String || value.String.Length > max) throw new ScriptRuntimeException("Expected a bounded string."); return value.String; }
    private static int Integer(DynValue value, int min, int max)
    { if (value.Type != DataType.Number || !double.IsFinite(value.Number) || value.Number != Math.Truncate(value.Number) || value.Number < min || value.Number > max) throw new ScriptRuntimeException($"Expected an integer from {min} to {max}."); return (int)value.Number; }
}
