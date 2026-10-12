using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DualSenseTweaker.ControllerBridge;
using DualSenseTweaker.MacroGamer.Core;

namespace DualSenseTweaker.MacroGamer.Presentation;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
    public void RefreshAll() => Raise("");
}
public sealed class AsyncCommand(Func<Task> run, Action<Exception> error, Func<bool>? canRun = null) : ObservableModel, ICommand
{
    private bool _busy;
    public bool Busy => _busy;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_busy && (canRun?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return; _busy = true; Raise(nameof(Busy)); Refresh();
        try { await run(); } catch (Exception ex) { error(ex); }
        finally { _busy = false; Raise(nameof(Busy)); Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed record Choice<T>(T Value, string Label, bool Available = true);
public sealed record NavigationItem(string Key, string Title);

public sealed class ActionEditor : ObservableModel
{
    private ActionKind _kind; private string _code = "E", _duration = "60", _x = "0", _y = "0";
    public ActionKind Kind { get => _kind; set { if (Set(ref _kind, value)) { Raise(nameof(Label)); Raise(nameof(UsesControl)); Raise(nameof(UsesDuration)); Raise(nameof(UsesMouseValues)); } } }
    public string Code { get => _code; set { Set(ref _code, value); Raise(nameof(Label)); } }
    public string Duration { get => _duration; set { Set(ref _duration, value); Raise(nameof(Label)); } }
    public string X { get => _x; set { Set(ref _x, value); Raise(nameof(Label)); } }
    public string Y { get => _y; set { Set(ref _y, value); Raise(nameof(Label)); } }
    public bool UsesControl => Kind is ActionKind.KeyDown or ActionKind.KeyUp or ActionKind.KeyTap or ActionKind.MouseDown or ActionKind.MouseUp or ActionKind.MouseTap;
    public bool UsesDuration => Kind is ActionKind.Delay or ActionKind.KeyTap or ActionKind.MouseTap;
    public bool UsesMouseValues => Kind is ActionKind.MouseMove or ActionKind.MouseWheel;
    public string Label => Kind switch { ActionKind.Delay => $"{Kind} · {Duration} ms", ActionKind.MouseMove => $"{Kind} · {X}, {Y}", ActionKind.MouseWheel => $"{Kind} · {Y}", _ => $"{Kind} · {Code}" + (UsesDuration ? $" · {Duration} ms" : "") };
    public ActionEditor() { }
    public ActionEditor(MacroAction action) { _kind = action.Kind; _code = action.Code; _duration = action.DurationMs.ToString(CultureInfo.InvariantCulture); _x = action.X.ToString(CultureInfo.InvariantCulture); _y = action.Y.ToString(CultureInfo.InvariantCulture); }
    public MacroAction ToAction()
    {
        var action = new MacroAction { Kind = Kind, Code = Code, DurationMs = Number(Duration, "Duration"), X = Number(X, "X"), Y = Number(Y, "Y") };
        action.Validate(); return action;
    }
    internal static int Number(string text, string field) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value : throw new InvalidDataException($"{field}: enter a whole number.");
}
public sealed class MacroEditor : ObservableModel
{
    public Guid Id { get; init; } = Guid.NewGuid();
    private string _name = "New macro", _buttons = "R1", _required = "", _analog = "R2", _held = "250", _threshold = "128", _repeat = "1", _maximum = "30000";
    private bool _enabled, _repeatActive; private TriggerKind _trigger = TriggerKind.Press; private RunMode _mode;
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public TriggerKind Trigger { get => _trigger; set => Set(ref _trigger, value); }
    public string Buttons { get => _buttons; set => Set(ref _buttons, value); }
    public string RequiredButtons { get => _required; set => Set(ref _required, value); }
    public string AnalogTrigger { get => _analog; set => Set(ref _analog, value); }
    public string HeldMs { get => _held; set => Set(ref _held, value); }
    public string Threshold { get => _threshold; set => Set(ref _threshold, value); }
    public RunMode Mode { get => _mode; set => Set(ref _mode, value); }
    public string RepeatCount { get => _repeat; set => Set(ref _repeat, value); }
    public bool RepeatWhileActive { get => _repeatActive; set => Set(ref _repeatActive, value); }
    public string MaxRuntimeMs { get => _maximum; set => Set(ref _maximum, value); }
    public ObservableCollection<ActionEditor> Actions { get; } = [];
    public MacroEditor() => Actions.Add(new(new() { Kind = ActionKind.KeyTap }));
    public MacroEditor(MacroDefinition macro)
    {
        Id = macro.Id; _name = macro.Name; _enabled = macro.Enabled; _trigger = macro.Trigger.Kind;
        _buttons = ButtonText(macro.Trigger.Buttons); _required = ButtonText(macro.Trigger.RequiredButtons); _analog = macro.Trigger.AnalogTrigger;
        _held = macro.Trigger.HeldMs.ToString(CultureInfo.InvariantCulture); _threshold = macro.Trigger.Threshold.ToString(CultureInfo.InvariantCulture);
        _mode = macro.Mode; _repeat = macro.RepeatCount.ToString(CultureInfo.InvariantCulture); _repeatActive = macro.RepeatWhileActive;
        _maximum = macro.MaxRuntimeMs.ToString(CultureInfo.InvariantCulture); foreach (var action in macro.Actions) Actions.Add(new(action));
    }
    public MacroDefinition ToDefinition()
    {
        var macro = new MacroDefinition
        {
            Id = Id, Name = Name, Enabled = Enabled, Mode = Mode, RepeatCount = ActionEditor.Number(RepeatCount, "Repeat count"),
            RepeatWhileActive = RepeatWhileActive, MaxRuntimeMs = ActionEditor.Number(MaxRuntimeMs, "Maximum runtime"), Actions = Actions.Select(a => a.ToAction()).ToArray(),
            Trigger = new() { Kind = Trigger, Buttons = ParseButtons(Buttons), RequiredButtons = ParseButtons(RequiredButtons), AnalogTrigger = AnalogTrigger,
                HeldMs = ActionEditor.Number(HeldMs, "Hold duration"), Threshold = ActionEditor.Number(Threshold, "Threshold") },
        };
        macro.Validate(); return macro;
    }
    public static PadButtons ParseButtons(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PadButtons.None;
        PadButtons result = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<PadButtons>(part, true, out var value) || !Enum.IsDefined(value) || value == PadButtons.None)
                throw new InvalidDataException($"Unknown controller button: {part}");
            result |= value;
        }
        return result;
    }
    private static string ButtonText(PadButtons buttons) => buttons == PadButtons.None ? "" : string.Join('+', Enum.GetValues<PadButtons>().Where(b => b != 0 && (buttons & b) == b));
}
public sealed class ScriptEditor : ObservableModel
{
    public Guid Id { get; init; } = Guid.NewGuid();
    private string _name = "New script", _source = "on_press(\"R1\", function()\n    key_tap(\"E\", 60)\nend)"; private bool _enabled;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Source { get => _source; set => Set(ref _source, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public ScriptEditor() { }
    public ScriptEditor(ScriptDefinition script) { Id = script.Id; _name = script.Name; _source = script.Source; _enabled = script.Enabled; }
    public ScriptDefinition ToDefinition() { var value = new ScriptDefinition { Id = Id, Name = Name, Source = Source, Enabled = Enabled }; value.Validate(); return value; }
}
public sealed class RecordingMapping : ObservableModel
{
    private string _button = "R1", _key = "E";
    public string Button { get => _button; set => Set(ref _button, value); }
    public string Key { get => _key; set => Set(ref _key, value); }
    public RecordingMapping() { }
    public RecordingMapping(string button, string key) { _button = button; _key = key; }
}
public interface IUserInteraction
{
    string? OpenFile(string filter);
    string? SaveFile(string name, string filter);
    bool Confirm(string message);
    void OpenFolder(string path);
    void LaunchTweaker();
}
