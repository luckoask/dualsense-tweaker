using DualSenseTweaker.ControllerBridge;

namespace DualSenseTweaker.MacroGamer.Core;

public sealed record InputChanges(PadFrame? Previous, PadFrame Current, PadButtons Pressed, PadButtons Released, bool Connected);

/// <summary>One consumer owns this detector. Identity/session/sequence prevent stale and cross-controller transitions.</summary>
public sealed class InputTransitionDetector
{
    private readonly Dictionary<string, PadFrame> _previous = new(StringComparer.OrdinalIgnoreCase);
    public InputChanges? Update(PadFrame frame)
    {
        frame.Validate();
        _previous.TryGetValue(frame.ControllerId, out var previous);
        if (previous?.SessionId != frame.SessionId || previous?.ConnectionId != frame.ConnectionId) previous = null;
        if (previous is not null && (frame.Sequence <= previous.Sequence || frame.TimestampUs < previous.TimestampUs)) return null;
        _previous[frame.ControllerId] = frame;
        // The first sample is a baseline, not a fabricated press of buttons held before arming.
        return new(previous, frame, previous is null ? PadButtons.None : frame.Buttons & ~previous.Buttons,
            previous is null ? PadButtons.None : previous.Buttons & ~frame.Buttons, previous is null);
    }
    public void Disconnect(string id) => _previous.Remove(id);
    public void Reset() => _previous.Clear();
}

public sealed class TriggerMatcher
{
    private long? _heldSince;
    private bool _heldFired;
    public bool Match(MacroTrigger trigger, InputChanges changes)
    {
        var current = changes.Current;
        var previous = changes.Previous;
        bool down = current.IsPressed(trigger.Buttons);
        if (!down) { _heldSince = null; _heldFired = false; }
        if ((current.Buttons & trigger.RequiredButtons) != trigger.RequiredButtons) return false;
        switch (trigger.Kind)
        {
            case TriggerKind.Connected: return changes.Connected;
            case TriggerKind.Press:
            case TriggerKind.Combination: return previous is not null && down && !previous.IsPressed(trigger.Buttons);
            case TriggerKind.Release: return previous is not null && previous.IsPressed(trigger.Buttons) && !down;
            case TriggerKind.Held:
                if (!down) return false;
                // A first sample while already held cannot arm a hold trigger.
                if (changes.Connected) { _heldFired = true; return false; }
                if (!previous!.IsPressed(trigger.Buttons)) { _heldSince = current.TimestampUs; _heldFired = false; }
                if (!_heldFired && _heldSince.HasValue && current.TimestampUs - _heldSince >= trigger.HeldMs * 1000L)
                { _heldFired = true; return true; }
                return false;
            case TriggerKind.TriggerThreshold:
                if (previous is null) return false;
                return AnalogValue(current, trigger) >= trigger.Threshold && AnalogValue(previous, trigger) < trigger.Threshold;
            default: return false;
        }
    }
    public static bool IsActive(MacroTrigger trigger, PadFrame frame) =>
        (frame.Buttons & trigger.RequiredButtons) == trigger.RequiredButtons &&
        (trigger.Kind == TriggerKind.TriggerThreshold ? AnalogValue(frame, trigger) >= trigger.Threshold : frame.IsPressed(trigger.Buttons));
    private static byte AnalogValue(PadFrame frame, MacroTrigger trigger) => trigger.AnalogTrigger == "L2" ? frame.Analog.L2 : frame.Analog.R2;
}
