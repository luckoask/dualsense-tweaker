using System.Drawing;
using System.Windows.Forms;
using DualSenseTweaker.ViewModels;
using DualSenseTweaker.Core.State;
using DualSenseTweaker.Localization;

namespace DualSenseTweaker.Shell;

/// <summary>A profile entry in the tray menu.</summary>
public sealed record TrayProfile(string Name, bool IsActive, string? Shortcut, string? DisplayName = null);

/// <summary>Notification-area icon: open, quick profile switch, start with Windows, exit, and balloon notifications.</summary>
internal sealed class TrayIcon : INotifier, IDisposable
{
    private readonly NotifyIcon _icon;
    private ContextMenuStrip? _menu;
    private readonly Func<IReadOnlyList<TrayProfile>> _profiles;
    private readonly Action<string> _activate;
    private readonly Action _open;
    private readonly Func<bool> _startupEnabled;
    private readonly Action<bool> _setStartup;
    private readonly Action _exit;

    public TrayIcon(Icon icon, Func<IReadOnlyList<TrayProfile>> profiles, Action<string> activate, Action open,
        Func<bool> startupEnabled, Action<bool> setStartup, Action exit)
    {
        _profiles = profiles;
        _activate = activate;
        _open = open;
        _startupEnabled = startupEnabled;
        _setStartup = setStartup;
        _exit = exit;

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = L.Text("DualSense Tweaker od LCKS"),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _open();
        };
        _icon.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                PrepareMenu();
        };
    }

    /// <summary>
    /// Builds the right-click menu. WinForms menus take about a second to initialize, so the app
    /// calls this once the window is up instead of delaying the start.
    /// </summary>
    public void PrepareMenu()
    {
        if (_menu is not null)
            return;
        _menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false },
            ShowImageMargin = false,
            BackColor = DarkMenuColors.Background,
            ForeColor = DarkMenuColors.Text,
            Font = new Font("Segoe UI", 9.5f),
        };
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _icon.ContextMenuStrip = _menu;
    }

    public void Notify(string title, string message, bool warning = false) =>
        _icon.ShowBalloonTip(5000, title, message, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void SetStatus(string text) =>
        _icon.Text = text.Length > 127 ? text[..127] : text;

    public void RefreshAppearance()
    {
        if (_menu is null) return;
        _menu.BackColor = DarkMenuColors.Background;
        _menu.ForeColor = DarkMenuColors.Text;
        BuildMenu();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu?.Dispose();
    }

    private void BuildMenu()
    {
        if (_menu is null)
            return;
        _menu.Items.Clear();
        var open = new ToolStripMenuItem(L.Text("Otvoriť DualSense Tweaker"))
        {
            Font = new Font(_menu.Font, FontStyle.Bold),
            ForeColor = DarkMenuColors.Text,
        };
        open.Click += (_, _) => _open();
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripSeparator());

        foreach (var profile in _profiles())
        {
            // Check marks are drawn black by the renderer, so the active profile gets a dot instead.
            var item = new ToolStripMenuItem((profile.IsActive ? "●  " : "     ") + (profile.DisplayName ?? profile.Name))
            {
                ShortcutKeyDisplayString = profile.Shortcut,
                ForeColor = profile.IsActive ? DarkMenuColors.Accent : DarkMenuColors.Text,
            };
            var name = profile.Name;
            item.Click += (_, _) => _activate(name);
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());
        bool startup = _startupEnabled();
        var startupItem = new ToolStripMenuItem((startup ? "✓  " : "     ") + L.Text("Spúšťať s Windows")) { ForeColor = DarkMenuColors.Text };
        startupItem.Click += (_, _) => _setStartup(!startup);
        _menu.Items.Add(startupItem);
        var exit = new ToolStripMenuItem(L.Text("     Ukončiť")) { ForeColor = DarkMenuColors.Text };
        exit.Click += (_, _) => _exit();
        _menu.Items.Add(exit);
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        private static bool Light => ApplicationAppearance.CurrentTheme == AppTheme.Light;
        public static Color Background => Light ? Color.FromArgb(0xF6, 0xF8, 0xFC) : Color.FromArgb(0x12, 0x17, 0x22);
        public static Color Text => Light ? Color.FromArgb(0x17, 0x22, 0x38) : Color.FromArgb(0xE9, 0xED, 0xF5);
        public static Color Accent => Light ? Color.FromArgb(0x35, 0x6D, 0xDD) : Color.FromArgb(0x4F, 0x82, 0xF7);
        private static Color Hover => Light ? Color.FromArgb(0xE6, 0xED, 0xF9) : Color.FromArgb(0x1E, 0x25, 0x33);
        private static readonly Color Border = Color.FromArgb(0x26, 0x2D, 0x3D);

        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
