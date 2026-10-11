using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Shell;

/// <summary>Global "modifier + 1…9" shortcuts (RegisterHotKey) that switch to the profile in that slot.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int BaseId = 0xB000;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;
    private const byte VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly List<int> _registered = [];

    /// <param name="window">Its handle receives WM_HOTKEY; created here if the window was never shown (start in tray).</param>
    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
    }

    /// <summary>0-based slot of the pressed shortcut.</summary>
    public event EventHandler<int>? Pressed;

    public static string Describe(HotkeyModifiers modifiers) => modifiers switch
    {
        HotkeyModifiers.CtrlAltShift => "Ctrl+Alt+Shift",
        HotkeyModifiers.CtrlShift => "Ctrl+Shift",
        _ => "Ctrl+Alt",
    };

    /// <summary>
    /// What the current keyboard layout types for modifier+1…9. On many European layouts
    /// (SK, CZ, DE, PL…) Ctrl+Alt is AltGr, so a global Ctrl+Alt+3 would eat the "^" key.
    /// </summary>
    public static IReadOnlyDictionary<int, string> TypedCharacters(HotkeyModifiers modifiers)
    {
        var layout = GetKeyboardLayout(0);
        var state = new byte[256];
        state[VkControl] = 0x80;
        if (modifiers != HotkeyModifiers.CtrlShift)
            state[VkMenu] = 0x80;
        if (modifiers != HotkeyModifiers.CtrlAlt)
            state[VkShift] = 0x80;

        var typed = new Dictionary<int, string>();
        var buffer = new StringBuilder(8);
        for (int digit = 1; digit <= 9; digit++)
        {
            uint vk = (uint)('0' + digit);
            uint scan = MapVirtualKeyEx(vk, 0, layout);
            buffer.Clear();
            // Flag 4: do not change the keyboard state (dead keys), Windows 10 1607+.
            int result = ToUnicodeEx(vk, scan, state, buffer, buffer.Capacity, 4, layout);
            if (result != 0 && buffer.Length > 0 && !char.IsControl(buffer[0]))
                typed[digit] = buffer[0].ToString();
        }
        return typed;
    }

    /// <summary>First combination that types nothing on this layout, preferring Ctrl+Alt.</summary>
    public static HotkeyModifiers SuggestModifiers() =>
        Enum.GetValues<HotkeyModifiers>().FirstOrDefault(m => TypedCharacters(m).Count == 0, HotkeyModifiers.CtrlAlt);

    /// <returns>Digits that could not be registered (already taken by another application).</returns>
    public IReadOnlyList<int> Register(HotkeyModifiers modifiers)
    {
        Unregister();
        uint mods = ModControl | ModNoRepeat
                    | (modifiers != HotkeyModifiers.CtrlShift ? ModAlt : 0)
                    | (modifiers != HotkeyModifiers.CtrlAlt ? ModShift : 0);
        var failed = new List<int>();
        for (int digit = 1; digit <= 9; digit++)
        {
            if (RegisterHotKey(_hwnd, BaseId + digit, mods, (uint)('0' + digit)))
                _registered.Add(digit);
            else
                failed.Add(digit);
        }
        return failed;
    }

    public void Unregister()
    {
        foreach (int digit in _registered)
            UnregisterHotKey(_hwnd, BaseId + digit);
        _registered.Clear();
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (id > BaseId && id <= BaseId + 9)
            {
                handled = true;
                Pressed?.Invoke(this, id - BaseId - 1);
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);
}
