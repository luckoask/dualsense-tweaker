using DualSenseTweaker.ControllerBridge;

namespace DualSenseTweaker.MacroGamer.Core;

public sealed record RecordedInputPoint(long OffsetUs, PadFrame Frame);
public sealed record InputRecording
{
    public int FormatVersion { get; init; } = 1;
    public string ControllerId { get; init; } = "";
    public RecordedInputPoint[] Points { get; init; } = [];
    public void Validate()
    {
        if (FormatVersion != 1 || string.IsNullOrWhiteSpace(ControllerId) || ControllerId.Length > 1024 || Points is not { Length: >= 1 and <= 8192 })
            throw new InvalidDataException("Invalid recording format.");
        long previous = -1; Guid? session = null;
        foreach (var point in Points)
        {
            if (point is null || point.Frame is null || point.OffsetUs < previous || point.OffsetUs is < 0 or > 60_000_000 ||
                !string.Equals(point.Frame.ControllerId, ControllerId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid recording timeline or controller identity.");
            point.Frame.Validate();
            if (session is not null && point.Frame.SessionId != session) throw new InvalidDataException("Recording spans controller sessions.");
            session = point.Frame.SessionId; previous = point.OffsetUs;
        }
    }
    /// <summary>Explicit digital-button conversion; analog/touch traces remain in the recording.</summary>
    public MacroDefinition ToKeyboardMacro(string name, IReadOnlyDictionary<PadButtons, OutputControl> mappings)
    {
        Validate();
        foreach (var (button, control) in mappings)
        {
            if (button == PadButtons.None || ((uint)button & ((uint)button - 1)) != 0) throw new InvalidDataException("Mapping must use single buttons.");
            control.Validate();
        }
        var actions = new List<MacroAction>();
        var held = new HashSet<OutputControl>(); long previousMs = 0; int previousOutputs = 0;
        foreach (var point in Points)
        {
            long nowMs = point.OffsetUs / 1000;
            int delay = (int)Math.Max(0, nowMs - previousMs - previousOutputs * 4L);
            if (delay > 0) actions.Add(new() { Kind = ActionKind.Delay, DurationMs = delay });
            var current = mappings.Where(m => point.Frame.IsPressed(m.Key)).Select(m => m.Value.Normalize()).ToHashSet();
            previousOutputs = 0;
            foreach (var control in held.Except(current).OrderBy(c => c.Code)) { actions.Add(Action(control, false)); previousOutputs++; }
            foreach (var control in current.Except(held).OrderBy(c => c.Code)) { actions.Add(Action(control, true)); previousOutputs++; }
            held = current; previousMs = nowMs;
        }
        foreach (var control in held) actions.Add(Action(control, false));
        if (actions.Count == 0) throw new InvalidDataException("Recording has no mapped digital actions. Choose button mappings or record button presses.");
        var macro = new MacroDefinition { Name = name, Actions = actions.ToArray(), Trigger = new() { Kind = TriggerKind.Manual }, MaxRuntimeMs = 60_000 };
        macro.Validate(); return macro;
    }
    private static MacroAction Action(OutputControl control, bool pressed) => new()
    {
        Kind = control.Kind == ControlKind.Keyboard ? pressed ? ActionKind.KeyDown : ActionKind.KeyUp : pressed ? ActionKind.MouseDown : ActionKind.MouseUp,
        Code = control.Code, DurationMs = 0,
    };
}

public sealed class InputRecorder
{
    private readonly Lock _gate = new();
    private readonly List<RecordedInputPoint> _points = [];
    private string? _controller;
    private Guid? _session;
    private long? _start;
    private bool _recording;
    public bool IsRecording { get { lock (_gate) return _recording; } }
    public int Count { get { lock (_gate) return _points.Count; } }
    public void Start(string controllerId)
    {
        if (string.IsNullOrWhiteSpace(controllerId)) throw new ArgumentException("Select a real controller first.");
        lock (_gate) { _controller = controllerId; _session = null; _start = null; _points.Clear(); _recording = true; }
    }
    public void Observe(PadFrame frame)
    {
        lock (_gate)
        {
            if (!_recording || !string.Equals(frame.ControllerId, _controller, StringComparison.OrdinalIgnoreCase)) return;
            frame.Validate();
            _session ??= frame.SessionId; _start ??= frame.TimestampUs;
            if (_session != frame.SessionId || frame.TimestampUs < _start || frame.TimestampUs - _start > 60_000_000 || _points.Count >= 8192)
            { _recording = false; throw new InvalidOperationException("Recording stopped: controller session or resource limit changed."); }
            long offset = frame.TimestampUs - _start.Value;
            if (_points.Count != 0)
            {
                var last = _points[^1];
                if (frame.Sequence <= last.Frame.Sequence || frame.TimestampUs < last.Frame.TimestampUs) return;
                bool digital = frame.Buttons != last.Frame.Buttons;
                bool analog = offset - last.OffsetUs >= 16_000 && Significant(frame.Analog, last.Frame.Analog);
                bool touch = frame.First.Active != last.Frame.First.Active || frame.Second.Active != last.Frame.Second.Active ||
                    frame.First.Active && (frame.First.Id != last.Frame.First.Id || Math.Abs(frame.First.X - last.Frame.First.X) >= 8 || Math.Abs(frame.First.Y - last.Frame.First.Y) >= 8) ||
                    frame.Second.Active && (frame.Second.Id != last.Frame.Second.Id || Math.Abs(frame.Second.X - last.Frame.Second.X) >= 8 || Math.Abs(frame.Second.Y - last.Frame.Second.Y) >= 8);
                if (!digital && !analog && !touch) return;
            }
            _points.Add(new(offset, frame));
        }
    }
    public InputRecording Stop()
    {
        lock (_gate)
        {
            _recording = false;
            var recording = new InputRecording { ControllerId = _controller ?? "", Points = _points.ToArray() };
            recording.Validate(); return recording;
        }
    }
    private static bool Significant(PadAnalog a, PadAnalog b) => Math.Abs(a.LeftX - b.LeftX) >= 3 || Math.Abs(a.LeftY - b.LeftY) >= 3 ||
        Math.Abs(a.RightX - b.RightX) >= 3 || Math.Abs(a.RightY - b.RightY) >= 3 || Math.Abs(a.L2 - b.L2) >= 3 || Math.Abs(a.R2 - b.R2) >= 3;
}
