using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DualSenseTweaker.MacroGamer.Core;
using DualSenseTweaker.MacroGamer.Output;
using DualSenseTweaker.MacroGamer.Presentation;
using Catalog = DualSenseTweaker.MacroGamer.Localization.TextCatalog;

namespace DualSenseTweaker.MacroGamer.Windows.Tests;

public class DesktopTests
{
    [Fact]
    public async Task ActualWpfPagesRenderAndNativeOutputReachesOurOwnWindowAndStops()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { RunDesktopChecks(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    private static void RunDesktopChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "MacroGamerDesktop-" + Guid.NewGuid().ToString("N"));
        string evidence = Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? root, "artifacts", "validation", "desktop");
        Directory.CreateDirectory(evidence);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        application.Resources.MergedDictionaries.Add(new() { Source = new Uri("/MacroGamer;component/Themes/Dark.xaml", UriKind.Relative) });
        application.Resources.MergedDictionaries.Add(new() { Source = new Uri("/MacroGamer;component/Themes/MacroTheme.xaml", UriKind.Relative) });
        application.Resources.MergedDictionaries.Add(new() { Source = new Uri("/MacroGamer;component/Themes/Controls.xaml", UriKind.Relative) });
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        var runtime = new CompanionRuntime(Path.Combine(root, "unused-worker.dll"), root);
        var model = new MainViewModel(runtime, new NoDialogs(), action => application.Dispatcher.BeginInvoke(action), Catalog.Text) { EmergencyAvailable = true };
        var window = new MainWindow { DataContext = model, Width = 1280, Height = 850 };
        try
        {
            window.Show(); PumpUntil(() => window.IsLoaded);
            foreach (string language in new[] { "SK", "EN", "CZ" })
            foreach (string theme in new[] { "Dark", "Light" })
            {
                Catalog.Apply(application, language, theme);
                model.RefreshAll();
                for (int section = 0; section < 8; section++)
                {
                    model.Section = section; window.UpdateLayout(); Pump();
                    Assert.Equal(section, Descendants(window).OfType<TabControl>().Single().SelectedIndex);
                    foreach (var grid in Descendants(window).OfType<Grid>().Where(g => g.ColumnDefinitions.Count == 3))
                    {
                        var panels = grid.Children.OfType<StackPanel>().ToArray();
                        for (int i = 0; i < panels.Length; i++)
                        for (int j = i + 1; j < panels.Length; j++)
                        {
                            var first = panels[i].TransformToAncestor(grid).TransformBounds(new Rect(panels[i].RenderSize));
                            var second = panels[j].TransformToAncestor(grid).TransformBounds(new Rect(panels[j].RenderSize));
                            Assert.False(first.IntersectsWith(second), "Editable field groups overlap.");
                        }
                    }
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(evidence, $"{language}-{theme}-{section}.png")); encoder.Save(stream);
                }
            }
            Assert.Empty(errors.Errors);
            Assert.False(model.EngineEnabled); Assert.False(runtime.SourceOnline); Assert.Empty(runtime.Controllers);
            model.SelectedMacro!.Enabled = true; Await(model.SaveAsync()); Await(model.EnableCommand.ExecuteAsync());
            Await(model.RunMacroCommand.ExecuteAsync());
            var diagnostic = Assert.IsType<DiagnosticOutputBackend>(runtime.Engine.Ledger.Backend);
            PumpUntil(() => diagnostic.Events.Count >= 2);
            Await(model.StopAllCommand.ExecuteAsync()); Assert.False(model.EngineEnabled);
            NativeChecks(window, evidence);
            Await(model.DisposeAsync().AsTask());
        }
        finally
        {
            if (!runtime.Engine.Snapshot.Enabled) Await(runtime.DisposeAsync().AsTask());
            window.Close(); PresentationTraceSources.DataBindingSource.Listeners.Remove(errors); application.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void NativeChecks(Window window, string evidence)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        window.Activate(); SetForegroundWindow(handle); PumpUntil(() => GetForegroundWindow() == handle);
        var messages = new List<int>(); var source = HwndSource.FromHwnd(handle)!;
        IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
        {
            if ((message is 0x100 or 0x101 && w.ToInt32() == 0x87) || message is 0x20A or 0x20B or 0x20C) messages.Add(message);
            return IntPtr.Zero;
        }
        source.AddHook(Hook); GetCursorPos(out var previous);
        var backend = new WindowsSendInputBackend(); var engine = new MacroEngine(backend);
        try
        {
            var macro = new MacroDefinition { Enabled = true, Trigger = new() { Kind = TriggerKind.Manual }, Actions = [new() { Kind = ActionKind.KeyDown, Code = "F24" }, new() { Kind = ActionKind.Delay, DurationMs = 5000 }] };
            Await(engine.ConfigureAsync([macro], null)); engine.Enable(); Assert.True(engine.Start(macro.Id));
            PumpUntil(() => messages.Contains(0x100));
            Task? stopped = null;
            using var hotkey = new EmergencyHotkey(window, () => stopped = engine.StopAllAsync("Native global emergency hotkey"));
            Assert.True(hotkey.Available, $"Could not register emergency hotkey: {hotkey.Error}");
            try
            {
                foreach (string key in new[] { "CTRL", "SHIFT", "F10" }) Await(backend.SetControlAsync(OutputControl.Key(key), true, default).AsTask());
                PumpUntil(() => stopped is not null);
            }
            finally
            {
                foreach (string key in new[] { "F10", "SHIFT", "CTRL" }) Await(backend.SetControlAsync(OutputControl.Key(key), false, default).AsTask());
            }
            Await(stopped!); PumpUntil(() => messages.Contains(0x101));
            Assert.Empty(Await(engine.Ledger.HeldAsync())); Assert.False(engine.Snapshot.Enabled);
            var point = window.PointToScreen(new Point(100, 100)); SetCursorPos((int)point.X, (int)point.Y);
            Await(backend.SetControlAsync(OutputControl.Mouse("X2"), true, default).AsTask());
            Await(backend.SetControlAsync(OutputControl.Mouse("X2"), false, default).AsTask());
            Await(backend.MoveMouseAsync(2, 2, default).AsTask()); Await(backend.WheelAsync(120, default).AsTask());
            PumpUntil(() => messages.Contains(0x20B) && messages.Contains(0x20C) && messages.Contains(0x20A));
            File.WriteAllText(Path.Combine(evidence, "native-output.txt"), "PASS: SendInput delivered F24 down/up to own HWND; registered Ctrl+Shift+F10 received WM_HOTKEY and released held F24; X2 down/up and wheel messages received.\n");
        }
        finally { Await(engine.DisposeAsync().AsTask()); source.RemoveHook(Hook); SetCursorPos(previous.X, previous.Y); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var item in Descendants(child)) yield return item; } }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void PumpUntil(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Interactive Windows desktop check timed out."); Pump(); Thread.Yield(); }
        Pump();
    }
    private static T Await<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (message?.Contains("Error:", StringComparison.Ordinal) == true) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private sealed class NoDialogs : IUserInteraction
    {
        public string? OpenFile(string filter) => null; public string? SaveFile(string filter, string suggestedName) => null;
        public bool Confirm(string message) => true; public void OpenFolder(string path) { } public void LaunchTweaker() { }
        public string? PickExecutable() => null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
