using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Diagnostics;
using DualSenseTweaker.Core.Diagnostics;
using DualSenseTweaker.Core.State;
using DualSenseTweaker.Controls.Model;
using DualSenseTweaker.Core.Device;

namespace DualSenseTweaker.Controls;

/// <summary>
/// Live 3D DualSense: rotated by the fused gyro orientation, buttons/sticks/triggers/touches shown
/// as they happen, light bar in the current color. Drag to look around it, Ctrl + wheel to zoom,
/// double-click to return to the player's view.
/// </summary>
public sealed class Controller3DView : Border
{
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Quaternion), typeof(Controller3DView),
            new PropertyMetadata(Quaternion.Identity, (d, e) =>
            {
                var view = (Controller3DView)d;
                view._rotation.Quaternion = (Quaternion)e.NewValue;
                view.UpdateCamera();
            }));

    public static readonly DependencyProperty LightbarBrushProperty =
        DependencyProperty.Register(nameof(LightbarBrush), typeof(Brush), typeof(Controller3DView),
            new PropertyMetadata(null, (d, e) => ((Controller3DView)d).OnLightbarChanged(e.NewValue as Brush)));

    public static readonly DependencyProperty InputProperty =
        DependencyProperty.Register(nameof(Input), typeof(InputFrame), typeof(Controller3DView),
            new PropertyMetadata(InputFrame.Neutral, (d, e) => ((Controller3DView)d)._model?.Apply((InputFrame)e.NewValue)));

    public static readonly DependencyProperty PlayerLedsProperty = DependencyProperty.Register(nameof(PlayerLeds), typeof(PlayerLedMode), typeof(Controller3DView), new PropertyMetadata(PlayerLedMode.Player1, OnLedsChanged));
    public static readonly DependencyProperty MicLedProperty = DependencyProperty.Register(nameof(MicLed), typeof(MicLedMode), typeof(Controller3DView), new PropertyMetadata(MicLedMode.Off, OnLedsChanged));
    public static readonly DependencyProperty LedBrightnessProperty = DependencyProperty.Register(nameof(LedBrightness), typeof(LedBrightness), typeof(Controller3DView), new PropertyMetadata(LedBrightness.High, OnLedsChanged));

    private const double DefaultYaw = 0, DefaultPitch = 67, DefaultDistance = 3.75;
    private static readonly Point3D Target = new(0, 0, 0);

    private readonly Viewport3D _viewport = new() { ClipToBounds = true };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 38, UpDirection = new Vector3D(0, 1, 0) };
    private readonly QuaternionRotation3D _rotation = new();
    private readonly SolidColorBrush _glow = new(Color.FromRgb(0x4F, 0x82, 0xF7));
    private readonly Model3DGroup _controller = new();
    private DualSenseModel? _model;
    private double _yaw = DefaultYaw, _pitch = DefaultPitch, _distance = DefaultDistance;
    private Point? _dragFrom;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _renderSubscribed;

    public Controller3DView()
    {
        Background = Brushes.Transparent; // the whole area takes the mouse, not just the model
        Cursor = Cursors.SizeAll;
        SetResourceReference(ToolTipProperty, "LocalizedText:Ťahaním otočíš pohľad, Ctrl + koliesko priblíži, dvojklik vráti pohľad hráča.");
        Child = _viewport;
        _viewport.Camera = _camera;
        SizeChanged += (_, _) => UpdateCamera();
        Loaded += (_, _) => UpdateRenderSubscription();
        Unloaded += (_, _) => UpdateRenderSubscription();
        IsVisibleChanged += (_, _) => UpdateRenderSubscription();
        UpdateCamera();

        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(0x3C, 0x40, 0x4A)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0xC4, 0xC4, 0xC8), new Vector3D(-0.45, -1, -0.55)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0x3C, 0x50, 0x90), new Vector3D(0.85, -0.25, 0.45)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0x60, 0x3C, 0x24), new Vector3D(-0.7, 0.35, 0.55)));
        scene.Children.Add(BuildShadow());
        _controller.Transform = new RotateTransform3D(_rotation);
        scene.Children.Add(_controller);
        _viewport.Children.Add(new ModelVisual3D { Content = scene });

        ModelReady = LoadModelAsync();
    }

    internal Task ModelReady { get; }
    internal DualSenseModel? Model => _model;

    private async Task LoadModelAsync()
    {
        try
        {
            await Task.Run(DualSenseModel.Prepare).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                _model = new DualSenseModel(_glow);
                _model.Apply(Input);
                _model.SetLeds(PlayerLeds, MicLed, LedBrightness);
                _controller.Children.Add(_model.Root);
                UpdateCamera();
            });
        }
        catch (Exception ex)
        {
            Log.Error(L.Text("Načítanie 3D modelu ovládača zlyhalo"), ex);
            await Dispatcher.InvokeAsync(() => Child = new TextBlock
            {
                Text = L.Text("3D model sa nepodarilo načítať. Podrobnosti sú v logu aplikácie."),
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    public PlayerLedMode PlayerLeds { get => (PlayerLedMode)GetValue(PlayerLedsProperty); set => SetValue(PlayerLedsProperty, value); }
    public MicLedMode MicLed { get => (MicLedMode)GetValue(MicLedProperty); set => SetValue(MicLedProperty, value); }
    public LedBrightness LedBrightness { get => (LedBrightness)GetValue(LedBrightnessProperty); set => SetValue(LedBrightnessProperty, value); }
    private static void OnLedsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (Controller3DView)d;
        view._model?.SetLeds(view.PlayerLeds, view.MicLed, view.LedBrightness);
    }

    private void UpdateRenderSubscription()
    {
        bool wanted = IsLoaded && IsVisible;
        if (wanted == _renderSubscribed) return;
        _renderSubscribed = wanted;
        if (wanted) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }
    private void OnRendering(object? sender, EventArgs e) => _model?.UpdateLedBlink(_clock.Elapsed);

    public Quaternion Orientation
    {
        get => (Quaternion)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>Color of the light bar strips (bind the live lightbar).</summary>
    public Brush? LightbarBrush
    {
        get => (Brush?)GetValue(LightbarBrushProperty);
        set => SetValue(LightbarBrushProperty, value);
    }

    /// <summary>Latest input report: buttons, sticks, triggers, touches.</summary>
    public InputFrame Input
    {
        get => (InputFrame)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            (_yaw, _pitch, _distance) = (DefaultYaw, DefaultPitch, DefaultDistance);
            UpdateCamera();
            return;
        }
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from || !IsMouseCaptured)
            return;
        var now = e.GetPosition(this);
        _yaw -= (now.X - from.X) * 0.45;
        _pitch = Math.Clamp(_pitch + (now.Y - from.Y) * 0.35, -85, 85);
        _dragFrom = now;
        UpdateCamera();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    /// <summary>Ctrl + wheel zooms; a plain wheel keeps scrolling the page.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        _distance = Math.Clamp(_distance * (1 - e.Delta / 1200.0), 1.6, 6);
        UpdateCamera();
        e.Handled = true;
    }

    /// <summary>Camera angle in degrees (yaw around the controller, pitch above it) and distance.</summary>
    internal void SetView(double yaw, double pitch, double distance)
    {
        (_yaw, _pitch, _distance) = (yaw, pitch, distance);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        double aspect = ActualHeight > 0 ? ActualWidth / ActualHeight : 2;
        var direction = new Vector3D(Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch));
        var right = new Vector3D(Math.Cos(yaw), 0, -Math.Sin(yaw));
        var up = Vector3D.CrossProduct(direction, right);
        double tangent = Math.Tan(_camera.FieldOfView * Math.PI / 360);
        var bounds = _model?.Root.Bounds ?? new Rect3D(-0.8, -0.33, -0.55, 1.6, 0.66, 1.1);
        double fittedDistance = 1.3;
        // WPF's FOV is horizontal. Fit every rotated corner, including depth, so portrait poses stay visible.
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Point3D(bounds.X + ((corner & 1) != 0 ? bounds.SizeX : 0),
                bounds.Y + ((corner & 2) != 0 ? bounds.SizeY : 0),
                bounds.Z + ((corner & 4) != 0 ? bounds.SizeZ : 0));
            var relative = _controller.Transform.Transform(point) - Target;
            double horizontal = Math.Abs(Vector3D.DotProduct(relative, right));
            double vertical = Math.Abs(Vector3D.DotProduct(relative, up)) * aspect;
            double depth = Vector3D.DotProduct(relative, direction);
            fittedDistance = Math.Max(fittedDistance, depth + Math.Max(horizontal, vertical) * 1.14 / tangent + 0.15);
        }
        var offset = direction * (_distance / DefaultDistance * fittedDistance);
        _camera.Position = Target + offset;
        _camera.LookDirection = -offset;
    }

    private void OnLightbarChanged(Brush? brush)
    {
        if (brush is SolidColorBrush solid)
            _glow.Color = solid.Color;
    }

    private static GeometryModel3D BuildShadow()
    {
        var mesh = new MeshGeometry3D
        {
            Positions = { new(-1.2, -0.4, -1.0), new(1.2, -0.4, -1.0), new(1.2, -0.4, 1.05), new(-1.2, -0.4, 1.05) },
            TextureCoordinates = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) },
            TriangleIndices = { 0, 2, 1, 0, 3, 2 },
        };
        var brush = new RadialGradientBrush(Color.FromArgb(0x80, 0, 0, 0), Color.FromArgb(0, 0, 0, 0));
        return new GeometryModel3D(mesh, new DiffuseMaterial(brush));
    }
}
