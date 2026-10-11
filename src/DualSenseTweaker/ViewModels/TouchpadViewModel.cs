using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Input;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.ViewModels;

/// <summary>Touchpad as a mouse: clicking, cursor and scrolling settings plus the live pad view.</summary>
public sealed class TouchpadViewModel(PreferencesState preferences, TouchpadService service) : ObservableObject
{
    private ControllerInfo? _controller;
    private InputFrame _frame;

    private TouchpadSettings S => preferences.Current.Touchpad;

    public IReadOnlyList<Option<MouseButton>> ButtonOptions =>
    [
        new(MouseButton.Left, L.Text("Ľavé tlačidlo")),
        new(MouseButton.Right, L.Text("Pravé tlačidlo")),
        new(MouseButton.Middle, L.Text("Stredné tlačidlo")),
        new(MouseButton.None, L.Text("Bez akcie")),
    ];

    public bool Enabled
    {
        get => S.Enabled;
        set => Update(t => t with { Enabled = value });
    }

    public MouseButton Click1
    {
        get => S.Click1;
        set => Update(t => t with { Click1 = value });
    }

    public MouseButton Click2
    {
        get => S.Click2;
        set => Update(t => t with { Click2 = value });
    }

    public bool TapToClick
    {
        get => S.TapToClick;
        set => Update(t => t with { TapToClick = value });
    }

    public MouseButton Tap1
    {
        get => S.Tap1;
        set => Update(t => t with { Tap1 = value });
    }

    public MouseButton Tap2
    {
        get => S.Tap2;
        set => Update(t => t with { Tap2 = value });
    }

    public double CursorSpeed
    {
        get => S.CursorSpeed;
        set => Update(t => t with { CursorSpeed = (int)Math.Round(value) });
    }

    public double Acceleration
    {
        get => S.Acceleration;
        set => Update(t => t with { Acceleration = (int)Math.Round(value) });
    }

    public double ScrollSpeed
    {
        get => S.ScrollSpeed;
        set => Update(t => t with { ScrollSpeed = (int)Math.Round(value) });
    }

    public bool NaturalScrolling
    {
        get => S.NaturalScrolling;
        set => Update(t => t with { NaturalScrolling = value });
    }

    public bool Inertia
    {
        get => S.Inertia;
        set => Update(t => t with { Inertia = value });
    }

    public bool HorizontalScrolling
    {
        get => S.HorizontalScrolling;
        set => Update(t => t with { HorizontalScrolling = value });
    }

    public bool IsConnected => _controller is not null;

    public string StatusTitle => _controller is { } c
        ? L.Format($"{c.ModelName} pripojený cez {c.ConnectionName}")
        : L.Text("Ovládač nie je pripojený");

    public string StatusSubtitle => !Enabled ? L.Text("Myš cez dotykový panel je vypnutá.")
        : IsConnected ? L.Text("Dotykový panel ovláda myš.")
        : L.Text("Po pripojení bude dotykový panel ovládať myš.");

    /// <summary>Latest contacts for the live view.</summary>
    public InputFrame Frame => _frame;

    public void SetConnection(ControllerInfo? controller)
    {
        _controller = controller;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusSubtitle));
    }

    /// <summary>Polled by the UI timer.</summary>
    public void UpdateLive()
    {
        var frame = service.Latest;
        if (frame == _frame)
            return;
        _frame = frame;
        OnPropertyChanged(nameof(Frame));
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void Update(Func<TouchpadSettings, TouchpadSettings> change) =>
        preferences.Update(p => p with { Touchpad = change(p.Touchpad) });
}
