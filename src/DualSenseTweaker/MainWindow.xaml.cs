using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace DualSenseTweaker;

public partial class MainWindow : Window
{
    // DWM window attributes (Windows 11): caption, caption text and border color as COLORREF (0x00BBGGRR).
    private const int DwmBorderColor = 34, DwmCaptionColor = 35, DwmTextColor = 36;

    public MainWindow()
    {
        InitializeComponent();
        Logo.Source = LoadIconFrame(64);
        SourceInitialized += (_, _) => TintTitleBar();
    }

    /// <summary>An Image shows an .ico's first (smallest) frame; pick a sharp one for the logo instead.</summary>
    private static BitmapSource LoadIconFrame(int minimumSize)
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames
            .OrderBy(f => f.PixelWidth)
            .FirstOrDefault(f => f.PixelWidth >= minimumSize) ?? decoder.Frames[^1];
    }

    /// <summary>Title bar in the window's navy instead of the default gray; ignored before Windows 11.</summary>
    private void TintTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int caption = ColorRef("WindowBrush"), text = ColorRef("TextBrush"), border = ColorRef("PanelBorderBrush");
        DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
    }

    private int ColorRef(string resource)
    {
        var color = ((SolidColorBrush)FindResource(resource)).Color;
        return color.R | color.G << 8 | color.B << 16;
    }

    public void UpdateAppearance()
    {
        TintTitleBar();
        static void Redraw(DependencyObject element)
        {
            if (element is UIElement visual) visual.InvalidateVisual();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                Redraw(VisualTreeHelper.GetChild(element, i));
        }
        Redraw(this);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
