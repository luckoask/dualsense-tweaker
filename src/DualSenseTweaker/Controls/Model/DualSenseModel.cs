using System.Windows.Media;
using System.Windows.Media.Media3D;
using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Controls.Model;

/// <summary>The detailed bundled DualSense, animated in the controller's IMU coordinate system.</summary>
internal sealed class DualSenseModel
{
    private const double PressDepth = 0.012, StickTilt = 20, TriggerPull = 24;
    private static readonly Color Accent = Color.FromRgb(79, 130, 247);
    private static readonly Lazy<GlbModel> Shared = new(GlbModel.Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly IReadOnlyDictionary<string, ControllerButtons> ButtonMap = new Dictionary<string, ControllerButtons>
    {
        ["up"] = ControllerButtons.DpadUp, ["down"] = ControllerButtons.DpadDown,
        ["left"] = ControllerButtons.DpadLeft, ["right"] = ControllerButtons.DpadRight,
        ["triangle"] = ControllerButtons.Triangle, ["circle"] = ControllerButtons.Circle,
        ["cross"] = ControllerButtons.Cross, ["square"] = ControllerButtons.Square,
        ["l1"] = ControllerButtons.L1, ["r1"] = ControllerButtons.R1,
        ["left-stick"] = ControllerButtons.L3, ["right-stick"] = ControllerButtons.R3,
        ["ps"] = ControllerButtons.PS, ["mute"] = ControllerButtons.Mute,
        ["create"] = ControllerButtons.Create, ["options"] = ControllerButtons.Options,
        ["touchpad"] = ControllerButtons.Touchpad,
    };
    private readonly Dictionary<string, MovingPart> _controls = new(StringComparer.Ordinal);
    private readonly SolidColorBrush[] _playerLights = Enumerable.Range(0, 5).Select(_ => new SolidColorBrush(Colors.Black)).ToArray();
    private readonly SolidColorBrush _micLight = new(Colors.Black);
    private readonly (TranslateTransform3D Position, ScaleTransform3D Visible, QuaternionRotation3D Tilt)[] _fingers = new (TranslateTransform3D, ScaleTransform3D, QuaternionRotation3D)[2];
    private readonly MeshSurface _touchpad;
    private PlayerLedMode _playerMode;
    private MicLedMode _micMode;
    private LedBrightness _ledBrightness;

    public static void Prepare() => _ = Shared.Value;
    internal static GlbModel Asset => Shared.Value;

    public DualSenseModel(Brush lightbar)
    {
        var asset = Shared.Value;
        Root = new Model3DGroup();
        foreach (var group in asset.Parts.Where(p => p.Control != "" && p.Control != "lights").GroupBy(p => p.Control))
        {
            var bounds = Rect3D.Empty;
            foreach (var part in group) bounds.Union(part.Mesh.Bounds);
            var pivot = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
            if (group.Key.EndsWith("-stick", StringComparison.Ordinal)) pivot.Y = asset.StickPivotHeight;
            if (group.Key is "l2" or "r2")
            {
                pivot.Y = bounds.Y + bounds.SizeY - asset.Scale * 0.1;
                pivot.Z = bounds.Z + asset.Scale * 0.1;
            }
            var moving = new MovingPart(pivot);
            foreach (var part in group.OrderBy(p => p.Material.Transparent)) moving.Root.Children.Add(Geometry(part, moving.Glows));
            if (group.Key is "cross" or "circle" or "square" or "triangle")
                moving.Root.Children.Add(ControllerMarkings.Face(group.Key, group.Single(p => p.Part == "cap").Mesh));
            if (group.Key is "l1" or "r1" or "l2" or "r2")
                moving.Root.Children.Add(ControllerMarkings.Shoulder(group.Key, group.First().Mesh));
            _controls.Add(group.Key, moving);
            Root.Children.Add(moving.Root);
        }
        foreach (var part in asset.Parts.Where(p => p.Control == "").OrderBy(p => p.Material.Transparent)) Root.Children.Add(Geometry(part));
        foreach (var part in asset.Parts.Where(p => p.Control == "lights"))
        {
            if (part.Part == "player-indicator") AddPlayerLights(part.Mesh);
            else Root.Children.Add(new GeometryModel3D(part.Mesh, GlowMaterial(lightbar)) { BackMaterial = GlowMaterial(lightbar) });
        }
        string[] required = [.. ButtonMap.Keys, "l2", "r2"];
        if (required.Any(id => !_controls.ContainsKey(id))) throw new InvalidOperationException(L.Text("3D model neobsahuje všetky ovládacie prvky."));
        _touchpad = new MeshSurface(asset.Parts.Single(p => p.Control == "touchpad" && p.Part == "surface").Mesh);
        var fingerMesh = Ring(0.018, 0.025);
        for (int i = 0; i < 2; i++)
        {
            var material = GlowMaterial(new SolidColorBrush(i == 0 ? Accent : Color.FromRgb(242, 136, 74)));
            var visible = new ScaleTransform3D(0, 0, 0);
            var position = new TranslateTransform3D();
            var tilt = new QuaternionRotation3D();
            var transform = new Transform3DGroup();
            transform.Children.Add(visible);
            transform.Children.Add(new RotateTransform3D(tilt));
            transform.Children.Add(position);
            _controls["touchpad"].Root.Children.Add(new GeometryModel3D(fingerMesh, material) { BackMaterial = material, Transform = transform });
            _fingers[i] = (position, visible, tilt);
        }
        var muteBounds = _controls["mute"].Root.Bounds;
        var micMesh = Rectangle(muteBounds.X + muteBounds.SizeX * 0.15, muteBounds.X + muteBounds.SizeX * 0.85,
            muteBounds.Z + muteBounds.SizeZ * 0.3, muteBounds.Z + muteBounds.SizeZ * 0.7, muteBounds.Y + muteBounds.SizeY + 0.001);
        _controls["mute"].Root.Children.Add(new GeometryModel3D(micMesh, GlowMaterial(_micLight)));
        SetLeds(PlayerLedMode.Player1, MicLedMode.Off, LedBrightness.High);
        Apply(InputFrame.Neutral);
    }

    public Model3DGroup Root { get; }
    internal Model3DGroup Control(string name) => _controls[name].Root;
    internal Point3D FingerPosition(int index) => new(_fingers[index].Position.OffsetX, _fingers[index].Position.OffsetY, _fingers[index].Position.OffsetZ);
    internal bool FingerVisible(int index) => _fingers[index].Visible.ScaleX != 0;

    public void Apply(in InputFrame frame)
    {
        foreach (var (name, button) in ButtonMap)
        {
            bool down = frame.IsPressed(button) || name == "touchpad" && frame.TouchpadClick || name == "ps" && frame.PsButton;
            _controls[name].Press.OffsetY = down ? -PressDepth : 0;
            _controls[name].SetGlow(down ? 0.5 : 0);
        }
        Stick("left-stick", frame.Analog.LeftX, frame.Analog.LeftY);
        Stick("right-stick", frame.Analog.RightX, frame.Analog.RightY);
        Trigger("l2", frame.Analog.L2); Trigger("r2", frame.Analog.R2);
        Finger(0, frame.First); Finger(1, frame.Second);
    }

    private void Stick(string name, byte x, byte y)
    {
        static double Axis(byte value) => (value - 128.0) / (value < 128 ? 128 : 127);
        var stick = _controls[name];
        stick.X.Angle = Axis(y) * StickTilt; stick.Z.Angle = -Axis(x) * StickTilt;
    }

    private void Trigger(string name, byte value)
    {
        double amount = value / 255.0;
        _controls[name].X.Angle = -amount * TriggerPull; _controls[name].SetGlow(amount * 0.5);
    }

    private void Finger(int index, TouchPoint point)
    {
        var (position, visible, tilt) = _fingers[index];
        visible.ScaleX = visible.ScaleY = visible.ScaleZ = point.Active ? 1 : 0;
        if (!point.Active) return;
        double u = Math.Clamp(point.X / (double)(InputFrame.TouchpadWidth - 1), 0, 1);
        double v = Math.Clamp(point.Y / (double)(InputFrame.TouchpadHeight - 1), 0, 1);
        var at = _touchpad.Point(u, v);
        // Match the local slope so the far edge of the marker cannot sink below the touchpad.
        const double sample = 0.01;
        var across = _touchpad.Point(Math.Min(u + sample, 1), v) - _touchpad.Point(Math.Max(u - sample, 0), v);
        var along = _touchpad.Point(u, Math.Min(v + sample, 1)) - _touchpad.Point(u, Math.Max(v - sample, 0));
        var normal = Vector3D.CrossProduct(along, across);
        if (normal.LengthSquared < 1e-12) normal = new Vector3D(0, 1, 0);
        normal.Normalize();
        var up = new Vector3D(0, 1, 0);
        var axis = Vector3D.CrossProduct(up, normal);
        tilt.Quaternion = axis.LengthSquared > 1e-12 ? new Quaternion(axis, Vector3D.AngleBetween(up, normal)) : Quaternion.Identity;
        position.OffsetX = at.X;
        position.OffsetY = at.Y + 0.003;
        position.OffsetZ = at.Z;
    }

    public void SetLeds(PlayerLedMode player, MicLedMode mic, LedBrightness brightness)
    {
        (_playerMode, _micMode, _ledBrightness) = (player, mic, brightness); UpdateLedBlink(TimeSpan.Zero);
    }

    public void UpdateLedBlink(TimeSpan time)
    {
        byte level = _ledBrightness switch { LedBrightness.Medium => 145, LedBrightness.Low => 85, _ => 255 };
        byte mask = _playerMode.ToLedMask();
        for (int i = 0; i < 5; i++) _playerLights[i].Color = (mask & (1 << i)) != 0 ? Color.FromRgb(level, level, level) : Color.FromRgb(18, 20, 25);
        bool lit = _micMode == MicLedMode.On || _micMode == MicLedMode.Blink && time.TotalMilliseconds % 1000 < 500;
        _micLight.Color = lit ? Color.FromRgb(level, (byte)(level * 0.38), 0) : Colors.Black;
    }

    private void AddPlayerLights(MeshGeometry3D mesh)
    {
        var bins = Enumerable.Range(0, 5).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
        {
            double x = (mesh.Positions[mesh.TriangleIndices[i]].X + mesh.Positions[mesh.TriangleIndices[i + 1]].X + mesh.Positions[mesh.TriangleIndices[i + 2]].X) / 3;
            int slot = Math.Clamp((int)((x - mesh.Bounds.X) / mesh.Bounds.SizeX * 5), 0, 4);
            bins[slot].AddRange([mesh.TriangleIndices[i], mesh.TriangleIndices[i + 1], mesh.TriangleIndices[i + 2]]);
        }
        for (int i = 0; i < 5; i++)
        {
            var part = new MeshGeometry3D { Positions = mesh.Positions, Normals = mesh.Normals, TriangleIndices = new Int32Collection(bins[i]) };
            part.Freeze(); Root.Children.Add(new GeometryModel3D(part, GlowMaterial(_playerLights[i])));
        }
    }

    private static GeometryModel3D Geometry(GlbPart part, List<SolidColorBrush>? glows = null)
    {
        Brush color = part.Material.Color;
        if (part.Control == "ps") color = new SolidColorBrush(Color.FromRgb(22, 24, 30));
        if (part.Name.StartsWith("ps-mount", StringComparison.Ordinal) || part.Name.StartsWith("mute-mount", StringComparison.Ordinal) || part.Name.StartsWith("white-shell-detail", StringComparison.Ordinal)) color = new SolidColorBrush(Color.FromRgb(25, 28, 33));
        var material = new MaterialGroup(); material.Children.Add(new DiffuseMaterial(color));
        if (!part.Material.Transparent)
        {
            material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(60, 235, 239, 245)), 8 + (1 - part.Material.Roughness) * 65));
            if (glows is not null)
            {
                var glow = new SolidColorBrush(Colors.Black); glows.Add(glow); material.Children.Add(new EmissiveMaterial(glow));
            }
        }
        return new GeometryModel3D(part.Mesh, material) { BackMaterial = material };
    }

    private static MaterialGroup GlowMaterial(Brush brush)
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(Brushes.Black));
        material.Children.Add(new EmissiveMaterial(brush));
        return material;
    }

    private sealed class MovingPart
    {
        public MovingPart(Point3D pivot)
        {
            var transform = new Transform3DGroup(); transform.Children.Add(new RotateTransform3D(X, pivot)); transform.Children.Add(new RotateTransform3D(Z, pivot)); transform.Children.Add(Press); Root.Transform = transform;
        }
        public Model3DGroup Root { get; } = new();
        public TranslateTransform3D Press { get; } = new();
        public AxisAngleRotation3D X { get; } = new(new Vector3D(1, 0, 0), 0);
        public AxisAngleRotation3D Z { get; } = new(new Vector3D(0, 0, 1), 0);
        public List<SolidColorBrush> Glows { get; } = [];
        public void SetGlow(double amount)
        {
            var color = Color.FromRgb((byte)(Accent.R * amount), (byte)(Accent.G * amount), (byte)(Accent.B * amount)); foreach (var glow in Glows) glow.Color = color;
        }
    }

    private static MeshGeometry3D Rectangle(double left, double right, double front, double back, double y)
    {
        var mesh = new MeshGeometry3D { Positions = { new(left, y, front), new(right, y, front), new(right, y, back), new(left, y, back) }, TriangleIndices = { 0, 2, 1, 0, 3, 2 } }; mesh.Freeze(); return mesh;
    }

    private static MeshGeometry3D Ring(double inner, double outer)
    {
        const int segments = 40;
        var mesh = new MeshGeometry3D();
        for (int i = 0; i <= segments; i++)
        {
            double angle = i * Math.PI * 2 / segments;
            mesh.Positions.Add(new Point3D(Math.Cos(angle) * outer, 0, Math.Sin(angle) * outer)); mesh.Positions.Add(new Point3D(Math.Cos(angle) * inner, 0, Math.Sin(angle) * inner));
            if (i < segments) foreach (int index in new[] { i * 2, i * 2 + 1, i * 2 + 2, i * 2 + 1, i * 2 + 3, i * 2 + 2 }) mesh.TriangleIndices.Add(index);
        }
        mesh.Freeze(); return mesh;
    }

}
