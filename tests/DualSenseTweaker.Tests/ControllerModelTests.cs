using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using DualSenseTweaker.Controls;
using DualSenseTweaker.Controls.Model;
using DualSenseTweaker.Core.Device;

namespace DualSenseTweaker.Tests;

public class ControllerInputPreviewTests
{
    [Fact]
    public void ShortPressBetweenUiUpdatesIsShownOnceAndThenReleased()
    {
        var preview = new ControllerInputPreview();
        preview.Update(InputFrame.Neutral with { Buttons = ControllerButtons.Cross | ControllerButtons.L3, PsButton = true });
        preview.Update(InputFrame.Neutral);
        var shown = preview.Take();
        Assert.True(shown.IsPressed(ControllerButtons.Cross));
        Assert.True(shown.IsPressed(ControllerButtons.L3));
        Assert.True(shown.PsButton);
        Assert.Equal(InputFrame.Neutral, preview.Take());
    }

    [Fact]
    public void BriefTriggerAndTouchPreserveTheirActualPeakAndCoordinates()
    {
        var preview = new ControllerInputPreview();
        var touch = new TouchPoint(true, 12, 1350, 480);
        preview.Update(InputFrame.Neutral with { Analog = AnalogState.Centered with { R2 = 193 }, First = touch, TouchpadClick = true });
        preview.Update(InputFrame.Neutral);
        var shown = preview.Take();
        Assert.Equal(193, shown.Analog.R2);
        Assert.Equal(touch, shown.First);
        Assert.True(shown.TouchpadClick);
        Assert.Equal(InputFrame.Neutral, preview.Take());
    }

    [Fact]
    public void ControllerChangeResetsOldInputAndCentersSticks()
    {
        var preview = new ControllerInputPreview();
        preview.Update(InputFrame.Neutral with { Buttons = ControllerButtons.Circle, Analog = new AnalogState(0, 255, 255, 0, 255, 255) });
        preview.Reset();
        Assert.Equal(InputFrame.Neutral, preview.Take());
        Assert.Equal(AnalogState.Centered, preview.Take().Analog);
    }

    [Fact]
    public void HiddenWindowDoesNotReplayExpiredPresses()
    {
        var preview = new ControllerInputPreview();
        preview.Update(InputFrame.Neutral with { Buttons = ControllerButtons.Cross }, TimeSpan.FromMilliseconds(10));
        preview.Update(InputFrame.Neutral, TimeSpan.FromSeconds(5));
        Assert.Equal(InputFrame.Neutral, preview.Take());
    }
}

public class ControllerModelTests
{
    [Fact]
    public void ImportedGeometryKeepsEveryTriangleAndAllNamedControls() => Sta(() =>
    {
        var asset = DualSenseModel.Asset;
        Assert.Equal(81, asset.Parts.Count);
        Assert.Equal(104_240, asset.Parts.Sum(p => p.Mesh.TriangleIndices.Count / 3));
        Assert.Equal(1.6, asset.Bounds.SizeX, 6);
        Assert.All(asset.Parts, p => Assert.True(p.Mesh.IsFrozen));
        var model = new DualSenseModel(Brushes.Blue);
        foreach (string name in new[] { "up", "down", "left", "right", "cross", "circle", "square", "triangle", "l1", "r1", "l2", "r2", "left-stick", "right-stick", "touchpad", "ps", "mute", "create", "options" })
            Assert.NotEmpty(model.Control(name).Children);
        Assert.True(model.Control("left-stick").Bounds.X < 0);
        Assert.True(model.Control("right-stick").Bounds.X > 0);
        Assert.True(model.Control("triangle").Bounds.Z < model.Control("cross").Bounds.Z);
        Assert.True(model.Control("circle").Bounds.X > model.Control("square").Bounds.X);
    });

    public static IEnumerable<object[]> Buttons =>
    [
        [ControllerButtons.Cross, "cross"], [ControllerButtons.Circle, "circle"], [ControllerButtons.Square, "square"], [ControllerButtons.Triangle, "triangle"],
        [ControllerButtons.DpadUp, "up"], [ControllerButtons.DpadDown, "down"], [ControllerButtons.DpadLeft, "left"], [ControllerButtons.DpadRight, "right"],
        [ControllerButtons.L1, "l1"], [ControllerButtons.R1, "r1"], [ControllerButtons.L3, "left-stick"], [ControllerButtons.R3, "right-stick"],
        [ControllerButtons.PS, "ps"], [ControllerButtons.Mute, "mute"], [ControllerButtons.Create, "create"], [ControllerButtons.Options, "options"], [ControllerButtons.Touchpad, "touchpad"],
    ];

    [Theory]
    [MemberData(nameof(Buttons))]
    public void PressMovesOnlyItsMatchingPhysicalPartAndReleaseRestoresIt(ControllerButtons button, string name) => Sta(() =>
    {
        var model = new DualSenseModel(Brushes.Blue);
        model.Apply(InputFrame.Neutral with { Buttons = button });
        Assert.Equal(-0.012, model.Control(name).Transform.Value.OffsetY, 6);
        foreach (var other in Buttons.Where(b => (string)b[1] != name))
            Assert.Equal(0, model.Control((string)other[1]).Transform.Value.OffsetY, 6);
        model.Apply(InputFrame.Neutral);
        Assert.Equal(Matrix3D.Identity, model.Control(name).Transform.Value);
    });

    [Fact]
    public void HidReportDrivesTheRightButtonsSticksAndAnalogTriggers() => Sta(() =>
    {
        var report = new byte[64]; report[0] = 1;
        report[1] = 255; report[2] = 128; report[3] = 128; report[4] = 0;
        report[5] = 64; report[6] = 193;
        report[8] = 0x28; // Cross, D-pad neutral
        report[9] = 0x41; // L1, L3
        report[10] = 0x02; // touchpad click
        report[33] = report[37] = 0x80;
        Assert.Equal(InputParseResult.Ok, InputReportParser.TryParse(report, ConnectionType.Usb, out _, out var input));
        var model = new DualSenseModel(Brushes.Blue); model.Apply(input);
        Assert.Equal(-0.012, model.Control("cross").Transform.Value.OffsetY, 6);
        Assert.Equal(-0.012, model.Control("l1").Transform.Value.OffsetY, 6);
        Assert.Equal(-0.012, model.Control("touchpad").Transform.Value.OffsetY, 6);
        var left = (Transform3DGroup)model.Control("left-stick").Transform;
        var right = (Transform3DGroup)model.Control("right-stick").Transform;
        Assert.Equal(-20, ((AxisAngleRotation3D)((RotateTransform3D)left.Children[1]).Rotation).Angle, 6);
        Assert.Equal(-20, ((AxisAngleRotation3D)((RotateTransform3D)right.Children[0]).Rotation).Angle, 6);
        var trigger = (Transform3DGroup)model.Control("r2").Transform;
        Assert.Equal(-193.0 / 255 * 24, ((AxisAngleRotation3D)((RotateTransform3D)trigger.Children[0]).Rotation).Angle, 6);
    });

    [Fact]
    public void TwoTouchPointsFollowActualTouchpadSurfaceAndDisappearOnRelease() => Sta(() =>
    {
        var model = new DualSenseModel(Brushes.Blue);
        model.Apply(InputFrame.Neutral with { First = new TouchPoint(true, 1, 100, 100), Second = new TouchPoint(true, 2, 1750, 950) });
        Assert.True(model.FingerVisible(0)); Assert.True(model.FingerVisible(1));
        Assert.True(model.FingerPosition(0).X < model.FingerPosition(1).X);
        Assert.True(model.FingerPosition(0).Z < model.FingerPosition(1).Z);
        Assert.All(new[] { model.FingerPosition(0), model.FingerPosition(1) }, p => Assert.True(double.IsFinite(p.Y)));
        model.Apply(InputFrame.Neutral);
        Assert.False(model.FingerVisible(0)); Assert.False(model.FingerVisible(1));
    });

    [Fact]
    public void ActualWpfViewerLoadsAndRendersNeutralPressedAnalogAndRotatedStates() => Sta(() =>
    {
        var view = new Controller3DView { Width = 960, Height = 480, Background = new SolidColorBrush(Color.FromRgb(18, 22, 31)) };
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timeout.Tick += (_, _) => frame.Continue = false;
        timeout.Start();
        _ = view.ModelReady.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); timeout.Stop();
        Assert.True(view.ModelReady.IsCompleted); Assert.NotNull(view.Model);
        byte[] neutral = Render(view, "controller-neutral");
        view.LightbarBrush = Brushes.Red;
        Assert.True(DifferentPixels(neutral, Render(view, "controller-red-lightbar")) > 100);
        view.LightbarBrush = new SolidColorBrush(Color.FromRgb(79, 130, 247));
        view.Input = InputFrame.Neutral with { Buttons = ControllerButtons.Cross | ControllerButtons.DpadUp | ControllerButtons.L3, First = new TouchPoint(true, 1, 600, 400) };
        byte[] pressed = Render(view, "controller-pressed");
        Assert.True(DifferentPixels(neutral, pressed) > 80);
        view.Input = InputFrame.Neutral with { Analog = new AnalogState(255, 40, 50, 240, 220, 193), Second = new TouchPoint(true, 2, 1500, 750) };
        byte[] analog = Render(view, "controller-analog");
        Assert.True(DifferentPixels(neutral, analog) > 80);
        view.Orientation = new Quaternion(new Vector3D(0, 0, 1), 25);
        Assert.True(DifferentPixels(analog, Render(view, "controller-gyro")) > 1000);
        view.Input = InputFrame.Neutral;
        foreach (var (axis, angle, name) in new[]
                 {
                     (new Vector3D(0, 0, 1), 90.0, "portrait"),
                     (new Vector3D(1, 0, 0), 125.0, "upside-down"),
                     (new Vector3D(1, 1, 1), 150.0, "diagonal"),
                 })
        {
            view.Orientation = new Quaternion(axis, angle);
            AssertBrightBodyHasFrameMargin(Render(view, "controller-" + name), 960, 480);
        }
        view.Input = InputFrame.Neutral; view.Orientation = Quaternion.Identity;
        view.SetView(150, 15, 3.75); Render(view, "controller-rear");
    });

    private static byte[] Render(Controller3DView view, string name)
    {
        view.Measure(new Size(view.Width, view.Height)); view.Arrange(new Rect(0, 0, view.Width, view.Height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)view.Width, (int)view.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var pixels = new byte[(int)view.Width * (int)view.Height * 4]; bitmap.CopyPixels(pixels, (int)view.Width * 4, 0);
        int bright = 0;
        for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] > 90 && pixels[i + 1] > 90 && pixels[i + 2] > 90) bright++;
        Assert.True(bright > 5000, $"The 3D render is empty or too dark: {bright} bright pixels.");
        if (Environment.GetEnvironmentVariable("DUALSENSE_MODEL_RENDER_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
        }
        return pixels;
    }

    private static int DifferentPixels(byte[] a, byte[] b)
    {
        int changed = 0;
        for (int i = 0; i < a.Length; i += 4) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) changed++;
        return changed;
    }

    private static void AssertBrightBodyHasFrameMargin(byte[] pixels, int width, int height)
    {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (x >= 12 && x < width - 12 && y >= 12 && y < height - 12) continue;
                int offset = (y * width + x) * 4;
                Assert.False(pixels[offset] > 90 && pixels[offset + 1] > 90 && pixels[offset + 2] > 90,
                    $"The controller is clipped at ({x}, {y}) in a rotated pose.");
            }
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF verification timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
