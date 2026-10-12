using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using DualSenseTweaker.MacroGamer.Localization;
using DualSenseTweaker.MacroGamer.Presentation;
using Microsoft.Win32;

namespace DualSenseTweaker.MacroGamer;

internal sealed class WindowsInteraction(Window window) : IUserInteraction
{
    public string? OpenFile(string filter) { var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true }; return dialog.ShowDialog(window) == true ? dialog.FileName : null; }
    public string? SaveFile(string name, string filter) { var dialog = new SaveFileDialog { FileName = name, Filter = filter, AddExtension = true, OverwritePrompt = true }; return dialog.ShowDialog(window) == true ? dialog.FileName : null; }
    public bool Confirm(string message) => MessageBox.Show(window, message, "Macro Gamer", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    public void LaunchTweaker()
    {
        string candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "DualSenseTweaker.exe"));
        if (!File.Exists(candidate))
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "DualSenseTweaker.sln"))) root = root.Parent;
            if (root is not null)
                foreach (var config in new[] { "Release", "Debug" })
                {
                    var path = Path.Combine(root.FullName, "src", "DualSenseTweaker", "bin", config, "net10.0-windows", "DualSenseTweaker.exe");
                    if (File.Exists(path)) { candidate = path; break; }
                }
        }
        if (!File.Exists(candidate)) throw new FileNotFoundException(TextCatalog.Text("DualSense Tweaker sa nenašiel. Oprav inštaláciu alebo ho spusti manuálne s --share-input."));
        var start = new ProcessStartInfo(candidate) { UseShellExecute = false }; start.ArgumentList.Add("--share-input"); Process.Start(start);
    }
    public static string? ForegroundExecutable()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint id); if (id == 0) return null;
        try { using var process = Process.GetProcessById((int)id); return process.ProcessName + ".exe"; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
