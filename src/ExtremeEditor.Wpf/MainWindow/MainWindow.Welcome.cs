using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private static readonly string RecentFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "recent-charts.json");

    private void ShowWelcomePage()
    {
        StopPlayback();
        EditorShellGrid.Visibility = Visibility.Collapsed;
        WelcomePage.Visibility = Visibility.Visible;
        RefreshWelcomeRecentFiles();
    }

    private void HideWelcomePage()
    {
        WelcomePage.Visibility = Visibility.Collapsed;
        EditorShellGrid.Visibility = Visibility.Visible;
    }

    private void WelcomeTabClick(object sender, RoutedEventArgs e) => ShowWelcomePage();

    private void WelcomeOpenClick(object sender, RoutedEventArgs e)
    {
        System.Windows.Input.ApplicationCommands.Open.Execute(null, this);
    }

    private IReadOnlyList<string> LoadRecentFiles()
    {
        try
        {
            if (!File.Exists(RecentFilePath))
                return [];
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(RecentFilePath)) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void RememberRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        try
        {
            string full = Path.GetFullPath(path);
            string[] items = new[] { full }
                .Concat(LoadRecentFiles())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFilePath)!);
            File.WriteAllText(RecentFilePath, JsonSerializer.Serialize(items));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        RefreshWelcomeRecentFiles();
    }

    private void RefreshWelcomeRecentFiles()
    {
        WelcomeRecentFilesHost.Children.Clear();
        string[] paths = LoadRecentFiles().Where(File.Exists).Take(12).ToArray();
        WelcomeRecentEmpty.Visibility = paths.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (string path in paths)
        {
            var button = new Button
            {
                Content = Path.GetFileName(path),
                ToolTip = path,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(12, 9, 12, 9)
            };
            button.Click += async (_, _) =>
            {
                if (!CanSwitchDocumentTabs())
                    return;
                EditorDocumentTab? existing = _documentTabs.FindByPath(path);
                if (existing is not null)
                    await ActivateDocumentTab(existing);
                else
                    await LoadLevelAsync(path);
            };
            WelcomeRecentFilesHost.Children.Add(button);
        }
    }
}
