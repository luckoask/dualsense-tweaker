using DualSenseTweaker.ControllerBridge;
using System.Text.Json.Serialization;

namespace DualSenseTweaker.MacroGamer.Core;

public enum ExecutionState { Idle, Armed, Running, Stopping, Stopped, Error }
public enum TriggerKind { Manual, Press, Release, Held, Combination, TriggerThreshold, Connected, Disconnected }
public enum RunMode { Once, Hold, Toggle }
public enum ActionKind { Delay, KeyDown, KeyUp, KeyTap, MouseDown, MouseUp, MouseTap, MouseMove, MouseWheel }
public enum ControlKind { Keyboard, MouseButton }
public enum OutputBackendKind { Diagnostic, KeyboardMouse, VirtualController }

public readonly record struct OutputControl(ControlKind Kind, string Code)
{
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SPACE", "ENTER", "TAB", "ESCAPE", "BACKSPACE", "DELETE", "INSERT", "HOME", "END",
        "PAGEUP", "PAGEDOWN", "UP", "DOWN", "LEFT", "RIGHT", "SHIFT", "CTRL", "ALT",
        "LSHIFT", "RSHIFT", "LCTRL", "RCTRL", "LALT", "RALT", "CAPSLOCK",
    };
    public static OutputControl Key(string code) => new OutputControl(ControlKind.Keyboard, code).Normalize();
    public static OutputControl Mouse(string code) => new OutputControl(ControlKind.MouseButton, code).Normalize();
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Code) || Code.Length > 16 || !Enum.IsDefined(Kind))
            throw new InvalidDataException("Invalid output control.");
        string normalized = Code.ToUpperInvariant();
        bool valid = Kind == ControlKind.MouseButton
            ? normalized is "LEFT" or "RIGHT" or "MIDDLE" or "X1" or "X2"
            : Keys.Contains(normalized) || normalized.Length == 1 && char.IsAsciiLetterOrDigit(normalized[0]) ||
              normalized.StartsWith('F') && int.TryParse(normalized.AsSpan(1), out var n) && n is >= 1 and <= 24;
        if (!valid) throw new InvalidDataException($"Unsupported output control: {Code}");
    }
    public OutputControl Normalize()
    {
        if (Code is null) throw new InvalidDataException("Output control is missing.");
        string code = Code.Trim().ToUpperInvariant();
        if (Kind == ControlKind.Keyboard)
        {
            code = code switch { "CTRL" => "LCTRL", "SHIFT" => "LSHIFT", "ALT" => "LALT", _ => code };
            if (code.StartsWith('F') && int.TryParse(code.AsSpan(1), out var number)) code = "F" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return this with { Code = code };
    }
    public override string ToString() => $"{Kind}:{Code}";
}

public sealed record MacroAction
{
    public ActionKind Kind { get; init; } = ActionKind.Delay;
    public string Code { get; init; } = "E";
    public int DurationMs { get; init; } = 60;
    public int X { get; init; }
    public int Y { get; init; }
    [JsonIgnore] public OutputControl Control => Kind is ActionKind.MouseDown or ActionKind.MouseUp or ActionKind.MouseTap
        ? OutputControl.Mouse(Code) : OutputControl.Key(Code);
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || DurationMs is < 0 or > 60_000 || X is < -10_000 or > 10_000 || Y is < -10_000 or > 10_000)
            throw new InvalidDataException("Action parameters exceed allowed limits.");
        if (Kind is ActionKind.KeyDown or ActionKind.KeyUp or ActionKind.KeyTap or ActionKind.MouseDown or ActionKind.MouseUp or ActionKind.MouseTap)
        {
            if (string.IsNullOrWhiteSpace(Code)) throw new InvalidDataException("Choose an output control.");
            Control.Validate();
        }
        if (Kind is ActionKind.KeyTap or ActionKind.MouseTap && DurationMs < 1)
            throw new InvalidDataException("A tap must last at least one millisecond.");
    }
}

public sealed record MacroTrigger
{
    public TriggerKind Kind { get; init; } = TriggerKind.Press;
    public PadButtons Buttons { get; init; } = PadButtons.R1;
    public PadButtons RequiredButtons { get; init; }
    public string AnalogTrigger { get; init; } = "R2";
    public int Threshold { get; init; } = 128;
    public int HeldMs { get; init; } = 250;
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Threshold is < 1 or > 255 || HeldMs is < 1 or > 60_000 ||
            ((uint)(Buttons | RequiredButtons) & ~((1u << 23) - 1)) != 0 || AnalogTrigger is not ("L2" or "R2"))
            throw new InvalidDataException("Invalid macro trigger.");
        if (Kind is TriggerKind.Press or TriggerKind.Release or TriggerKind.Held or TriggerKind.Combination && Buttons == PadButtons.None)
            throw new InvalidDataException("Choose at least one trigger button.");
    }
}

public sealed record MacroDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "New macro";
    public bool Enabled { get; init; }
    public MacroTrigger Trigger { get; init; } = new();
    public RunMode Mode { get; init; }
    public int RepeatCount { get; init; } = 1;
    public bool RepeatWhileActive { get; init; }
    public int MaxRuntimeMs { get; init; } = 30_000;
    public MacroAction[] Actions { get; init; } = [];
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || !Enum.IsDefined(Mode) ||
            RepeatCount is < 1 or > 1000 || MaxRuntimeMs is < 1 or > 60_000 || Actions is not { Length: >= 1 and <= 2048 } || Trigger is null)
            throw new InvalidDataException("Invalid macro or resource limits.");
        Trigger.Validate();
        if (Trigger.Kind == TriggerKind.Disconnected)
            throw new InvalidDataException("Disconnect is a stop notification, not an output trigger. Outputs are released on disconnect.");
        foreach (var action in Actions) (action ?? throw new InvalidDataException("Null macro action.")).Validate();
        if (Mode == RunMode.Hold && Trigger.Kind is not (TriggerKind.Press or TriggerKind.Combination or TriggerKind.Held or TriggerKind.TriggerThreshold))
            throw new InvalidDataException("Hold mode requires a button or threshold trigger.");
        if (RepeatWhileActive && Mode == RunMode.Once)
            throw new InvalidDataException("Repeat while active requires hold or toggle mode.");
    }
    public MacroDefinition Snapshot() => this with { Trigger = Trigger with { }, Actions = Actions.Select(a => a with { }).ToArray() };
}

public sealed record EngineSnapshot(ExecutionState State, bool Enabled, string? ControllerId, string[] ActiveMacros, string? LastError);
public sealed record DiagnosticEntry(DateTimeOffset At, string Level, string Message);
public sealed class DiagnosticLog(int capacity = 1000)
{
    private readonly Lock _gate = new();
    private readonly Queue<DiagnosticEntry> _entries = new();
    public void Write(string message, string level = "Info")
    {
        lock (_gate)
        {
            _entries.Enqueue(new(DateTimeOffset.UtcNow, level, message.Length > 4096 ? message[..4096] : message));
            while (_entries.Count > capacity) _entries.Dequeue();
        }
    }
    public IReadOnlyList<DiagnosticEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }
    public void Clear() { lock (_gate) _entries.Clear(); }
}
