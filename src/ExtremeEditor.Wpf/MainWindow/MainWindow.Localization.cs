using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private static readonly string LanguageSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "language.json");
    private string _languagePreference = "system";
    private bool _settingWelcomeLanguage;

    private static readonly IReadOnlyDictionary<string, (string English, string Japanese)> WelcomeTranslations =
        new Dictionary<string, (string, string)>
        {
            ["welcome"] = ("Welcome", "ようこそ"),
            ["new"] = ("New chart  (Ctrl+N)", "新しい譜面を作成  (Ctrl+N)"),
            ["open"] = ("Open chart  (Ctrl+O)", "譜面を開く  (Ctrl+O)"),
            ["recent"] = ("Recent charts", "最近開いた譜面"),
            ["empty"] = ("No recent charts yet.", "最近開いた譜面はありません"),
            ["language"] = ("Language", "言語")
        };

    private bool UseJapanese => _languagePreference switch
    {
        "ja" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja"
    };

    private string WelcomeText(string key)
    {
        if (!WelcomeTranslations.TryGetValue(key, out var entry))
            return key;
        return UseJapanese ? entry.Japanese : entry.English;
    }

    private void InitializeWelcomeLocalization()
    {
        try
        {
            if (File.Exists(LanguageSettingsPath))
            {
                string? preference = JsonSerializer.Deserialize<string>(
                    File.ReadAllText(LanguageSettingsPath));
                if (preference is "system" or "en" or "ja")
                    _languagePreference = preference;
            }
        }
        catch (Exception)
        {
            _languagePreference = "system";
        }

        _settingWelcomeLanguage = true;
        foreach (ComboBoxItem item in WelcomeLanguageSelector.Items)
        {
            if (string.Equals(item.Tag as string, _languagePreference, StringComparison.Ordinal))
            {
                WelcomeLanguageSelector.SelectedItem = item;
                break;
            }
        }
        _settingWelcomeLanguage = false;
        ApplyWelcomeLocalization();
    }

    private void WelcomeLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingWelcomeLanguage ||
            WelcomeLanguageSelector.SelectedItem is not ComboBoxItem selected ||
            selected.Tag is not string preference ||
            preference is not ("system" or "en" or "ja"))
            return;

        _languagePreference = preference;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LanguageSettingsPath)!);
            File.WriteAllText(LanguageSettingsPath, JsonSerializer.Serialize(preference));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        ApplyWelcomeLocalization();
    }

    private void ApplyWelcomeLocalization()
    {
        WelcomeSubtitle.Text = WelcomeText("welcome");
        WelcomeNewButton.Content = WelcomeText("new");
        WelcomeOpenButton.Content = WelcomeText("open");
        WelcomeRecentTitle.Text = WelcomeText("recent");
        WelcomeRecentEmpty.Text = WelcomeText("empty");
        WelcomeLanguageLabel.Text = WelcomeText("language");
        WelcomeTabButton.Content = WelcomeText("welcome");
    }
}
