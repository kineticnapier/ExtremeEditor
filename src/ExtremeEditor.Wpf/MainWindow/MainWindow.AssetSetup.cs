using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private bool _assetSetupRunning;
    private string? _adoFaiGameRoot;
    private bool _hasNeoCosmos;

    private void AssetSetupWindowLoaded(object sender, RoutedEventArgs e)
    {
        RefreshAssetSetupUi();
    }

    private async void SetupAssetsClick(object sender, RoutedEventArgs e)
    {
        if (_assetSetupRunning)
            return;

        if (_adoFaiGameRoot is null)
        {
            BrowseAdoFaiClick(sender, e);
            if (_adoFaiGameRoot is null)
                return;
        }

        string extractorPath = AssetSetupService.DefaultExtractorPath;
        if (!File.Exists(extractorPath))
        {
            StatusText.Text = $"Asset extractor not found: {extractorPath}";
            return;
        }

        _assetSetupRunning = true;
        SetupAssetsMenuItem.IsEnabled = false;
        BrowseAdoFaiMenuItem.IsEnabled = false;
        StatusText.Text = "Extracting ADOFAI assets…";

        try
        {
            AssetSetupRunResult result = await AssetSetupService.RunExtractorAsync(
                extractorPath,
                AssetSetupService.DefaultCacheRoot,
                _adoFaiGameRoot);

            if (result.ExitCode == 0 && result.CacheStatus.IsReady)
            {
                NativeViewport.ReloadAssets();
                StatusText.Text = $"Assets ready | ADOFAI {result.CacheStatus.GameVersion}";
            }
            else
            {
                string detail = LastNonEmptyLine(result.StandardError)
                    ?? LastNonEmptyLine(result.StandardOutput)
                    ?? result.CacheStatus.Error
                    ?? "unknown extractor error";
                StatusText.Text = $"Asset setup failed (exit {result.ExitCode}): {detail}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Asset setup failed: {ex.Message}";
        }
        finally
        {
            _assetSetupRunning = false;
            RefreshAssetSetupUi();
        }
    }

    private void BrowseAdoFaiClick(object sender, RoutedEventArgs e)
    {
        if (_assetSetupRunning)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Select A Dance of Fire and Ice installation folder",
            Multiselect = false
        };

        if (_adoFaiGameRoot is not null && Directory.Exists(_adoFaiGameRoot))
            dialog.InitialDirectory = _adoFaiGameRoot;

        if (dialog.ShowDialog(this) != true)
            return;

        if (!AdoFaiInstallationLocator.IsGameRoot(dialog.FolderName))
        {
            MessageBox.Show(
                this,
                "That folder does not look like an A Dance of Fire and Ice installation.",
                "ExtremeEditor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AdoFaiInstallationSettings.SavePath(
            AdoFaiInstallationSettings.DefaultSettingsDirectory,
            dialog.FolderName);
        _adoFaiGameRoot = Path.GetFullPath(dialog.FolderName);
        RefreshAssetSetupUi();
    }

    private void RefreshAssetSetupUi()
    {
        string? savedPath = AdoFaiInstallationSettings.LoadSavedPath(
            AdoFaiInstallationSettings.DefaultSettingsDirectory);
        _adoFaiGameRoot = AdoFaiInstallationLocator.FindInstalledGame(
            savedPath,
            GetDefaultSteamRoots());
        _hasNeoCosmos = _adoFaiGameRoot is not null &&
                        AdoFaiInstallationLocator.HasInstalledNeoCosmos(_adoFaiGameRoot);

        AssetCacheStatus cache = AssetSetupService.InspectCache(AssetSetupService.DefaultCacheRoot);
        string installationName = _adoFaiGameRoot is null
            ? "Not detected"
            : Path.GetFileName(_adoFaiGameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        AdoFaiPathMenuItem.Header = $"ADOFAI: {installationName}";
        AdoFaiPathMenuItem.ToolTip = _adoFaiGameRoot ?? "Use Browse for ADOFAI… to select the installation folder.";
        SetupAssetsMenuItem.Header = cache.IsReady ? "Rebuild Assets" : "Setup Assets";
        SetupAssetsMenuItem.IsEnabled = !_assetSetupRunning;
        BrowseAdoFaiMenuItem.IsEnabled = !_assetSetupRunning;
        RefreshEventPalette();
    }

    private static string[] GetDefaultSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddRegistryPath(roots, @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath");
        AddRegistryPath(roots, @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        AddRegistryPath(roots, @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath");

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            roots.Add(Path.Combine(programFilesX86, "Steam"));

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            roots.Add(Path.Combine(programFiles, "Steam"));

        return roots.ToArray();
    }

    private static void AddRegistryPath(HashSet<string> roots, string key, string valueName)
    {
        try
        {
            if (Registry.GetValue(key, valueName, null) is string value && !string.IsNullOrWhiteSpace(value))
                roots.Add(value.Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            // Registry lookup is only one discovery source; Browse remains available.
        }
    }

    private static string? LastNonEmptyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
    }
}
