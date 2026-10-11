using System.Windows;
using DualSenseTweaker.ViewModels;
using Microsoft.Win32;

namespace DualSenseTweaker.Shell;

internal sealed class FileDialogs : IFileDialogs
{
    private static string ProfileFilter => L.Text("Profil DualSense Tweaker (*.json)|*.json|Všetky súbory (*.*)|*.*");

    public string? PickProfileToImport()
    {
        var dialog = new OpenFileDialog { Title = L.Text("Načítať profil zo súboru"), Filter = ProfileFilter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickExportPath(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = L.Text("Uložiť profil do súboru"),
            Filter = ProfileFilter,
            FileName = suggestedName + ".json",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickExecutable()
    {
        var dialog = new OpenFileDialog { Title = L.Text("Vybrať spustiteľný súbor hry"), Filter = L.Text("Programy (*.exe)|*.exe") };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
        == MessageBoxResult.Yes;
}
