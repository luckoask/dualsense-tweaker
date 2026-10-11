using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Windows.Media;
using DualSenseTweaker.Controls;

namespace DualSenseTweaker.Tests;

public sealed class LocalizedTemplateTests
{
    [Fact]
    public void PaletteChangesUpdateTheWindowTemplatesAndCustomText()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                static ResourceDictionary Load(string name) => new()
                {
                    Source = new Uri($"pack://application:,,,/DualSenseTweaker;component/Themes/{name}.xaml"),
                };
                var window = new Window();
                var resources = window.Resources.MergedDictionaries;
                resources.Add(Load("Dark"));
                resources.Add(Load("Theme"));
                resources.Add(Load("Controls"));
                window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
                window.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                var grid = new StackPanel();
                grid.SetResourceReference(Panel.BackgroundProperty, "GridBrush");
                var text = new TrackedText { Text = "Settings" };
                text.SetResourceReference(TrackedText.ForegroundProperty, "TextBrush");
                var button = new Button { Content = "Open logs" };
                grid.Children.Add(text);
                grid.Children.Add(button);
                window.Content = grid;
                window.Measure(new Size(400, 300));
                window.Arrange(new Rect(0, 0, 400, 300));
                button.ApplyTemplate();
                var border = (Border)button.Template.FindName("Bd", button);
                var oldGrid = grid.Background;
                Assert.Equal(Color.FromRgb(0x0A, 0x0D, 0x14), ((SolidColorBrush)window.Background).Color);
                Assert.Equal(Color.FromRgb(0x11, 0x15, 0x20), ((SolidColorBrush)border.Background).Color);
                resources[0] = Load("Light");
                Assert.Equal(Color.FromRgb(0xF4, 0xF6, 0xFB), ((SolidColorBrush)window.Background).Color);
                Assert.Equal(Color.FromRgb(0x17, 0x22, 0x38), ((SolidColorBrush)window.Foreground).Color);
                Assert.Equal(((SolidColorBrush)window.Foreground).Color, ((SolidColorBrush)text.Foreground).Color);
                Assert.Equal(Color.FromRgb(0xF6, 0xF8, 0xFC), ((SolidColorBrush)border.Background).Color);
                Assert.NotSame(oldGrid, grid.Background);
                resources[0] = Load("Dark");
                Assert.Equal(Color.FromRgb(0x0A, 0x0D, 0x14), ((SolidColorBrush)window.Background).Color);
                Assert.Equal(Color.FromRgb(0x11, 0x15, 0x20), ((SolidColorBrush)border.Background).Color);
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The WPF theme test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void OptimizedControlTemplatesUpdateTheirLocalizedText()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var control = new Control();
                const string key = "LocalizedText:Nastavenia";
                control.Resources[key] = "Nastavenia";
                control.Template = (ControlTemplate)XamlReader.Parse("""
                    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                     xmlns:l="clr-namespace:DualSenseTweaker.Localization;assembly=DualSenseTweaker">
                        <TextBlock x:Name="Caption" Text="{l:Text Nastavenia}" />
                    </ControlTemplate>
                    """);
                control.ApplyTemplate();
                control.Measure(new Size(300, 100));
                var caption = (TextBlock)control.Template.FindName("Caption", control);
                Assert.Equal("Nastavenia", caption.Text);
                control.Resources[key] = "Settings";
                Assert.Equal("Settings", caption.Text);
                control.Resources[key] = "Nastavení";
                Assert.Equal("Nastavení", caption.Text);
            }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The WPF template test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
