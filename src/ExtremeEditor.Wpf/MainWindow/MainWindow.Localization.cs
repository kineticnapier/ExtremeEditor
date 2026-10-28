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


    private static readonly IReadOnlyDictionary<string, (string English, string Japanese)> Translations =
        new Dictionary<string, (string, string)>
        {
            ["welcome"] = ("Welcome", "ようこそ"),
            ["new"] = ("New chart  (Ctrl+N)", "新しい譜面を作成  (Ctrl+N)"),
            ["open"] = ("Open chart  (Ctrl+O)", "譜面を開く  (Ctrl+O)"),
            ["recent"] = ("Recent charts", "最近開いた譜面"),
            ["empty"] = ("No recent charts yet.", "最近開いた譜面はありません"),
            ["file"] = ("_File", "ファイル"),
            ["edit"] = ("_Edit", "編集"),
            ["view"] = ("_View", "表示"),
            ["tools"] = ("_Tools", "ツール"),
            ["help"] = ("_Help", "ヘルプ"),
            ["settings"] = ("_Settings", "設定"),
            ["language"] = ("_Language", "言語"),
            ["newmenu"] = ("_New Tab", "新しいタブ"),
            ["openmenu"] = ("_Open", "開く"),
            ["save"] = ("_Save", "保存"),
            ["saveas"] = ("Save _As", "名前を付けて保存"),
            ["export"] = ("Export _Video…", "動画を書き出す…"),
            ["exit"] = ("E_xit", "終了"),
            ["undo"] = ("_Undo", "元に戻す"),
            ["redo"] = ("_Redo", "やり直す"),
            ["cut"] = ("Cu_t", "切り取り"),
            ["copy"] = ("_Copy", "コピー"),
            ["paste"] = ("_Paste", "貼り付け"),
            ["goto"] = ("_Go to Floor…", "タイル番号へ移動…"),
            ["transform"] = ("_Transform", "変形"),
            ["angle"] = ("_Angle…", "角度…"),
            ["rotateleft"] = ("Rotate -90°", "左に90°回転"),
            ["rotateright"] = ("Rotate +90°", "右に90°回転"),
            ["fliph"] = ("Flip _Horizontal", "左右反転"),
            ["flipv"] = ("Flip _Vertical", "上下反転"),
            ["frame"] = ("_Frame", "全体表示"),
            ["follow"] = ("Follow _Player", "プレイヤーを追従"),
            ["diagnostics"] = ("Show _Diagnostics", "診断情報を表示"),
            ["assets"] = ("_Assets", "アセット"),
            ["browse"] = ("Browse for ADOFAI…", "ADOFAIの場所を指定…"),
            ["setup"] = ("Setup Assets", "アセットを設定"),
            ["about"] = ("_About ExtremeEditor", "ExtremeEditorについて"),
            ["play"] = ("Play", "再生"),
            ["stop"] = ("Stop", "停止"),
            ["speed"] = ("Speed", "速度"),
            ["hitsounds"] = ("Hitsounds", "打鍵音"),
            ["pause"] = ("Pause", "一時停止"),
            ["unsaved"] = ("Unsaved chart", "未保存の譜面"),
            ["closequestion"] = ("Save changes to {0} before closing?", "{0} の変更を保存して閉じますか？")
        };

    private bool UseJapanese => _languagePreference switch
    {
        "ja" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja"
    };

    private string WelcomeText(string key)
    {
        if (!Translations.TryGetValue(key, out var entry))
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

        ApplyWelcomeLocalization();
    }

    private void LanguageMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string preference } ||
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

        WelcomeTabButton.Content = WelcomeText("welcome");
        LanguageSystemMenu.IsChecked = _languagePreference == "system";
        LanguageEnglishMenu.IsChecked = _languagePreference == "en";
        LanguageJapaneseMenu.IsChecked = _languagePreference == "ja";
        FileMenu.Header = WelcomeText("file");
        EditMenu.Header = WelcomeText("edit");
        ViewMenu.Header = WelcomeText("view");
        ToolsMenu.Header = WelcomeText("tools");
        HelpMenu.Header = WelcomeText("help");
        FileSettingsMenu.Header = WelcomeText("settings");
        FileLanguageMenu.Header = WelcomeText("language");
        FileNewMenu.Header = WelcomeText("newmenu");
        FileOpenMenu.Header = WelcomeText("openmenu");
        FileSaveMenu.Header = WelcomeText("save");
        FileSaveAsMenu.Header = WelcomeText("saveas");
        ExportVideoMenuItem.Header = WelcomeText("export");
        ExitMenuItem.Header = WelcomeText("exit");
        EditUndoMenu.Header = WelcomeText("undo");
        EditRedoMenu.Header = WelcomeText("redo");
        EditCutMenu.Header = WelcomeText("cut");
        EditCopyMenu.Header = WelcomeText("copy");
        EditPasteMenu.Header = WelcomeText("paste");
        EditGoToMenu.Header = WelcomeText("goto");
        TransformMenu.Header = WelcomeText("transform");
        EditAngleMenu.Header = WelcomeText("angle");
        EditRotateLeftMenu.Header = WelcomeText("rotateleft");
        EditRotateRightMenu.Header = WelcomeText("rotateright");
        EditFlipHorizontalMenu.Header = WelcomeText("fliph");
        EditFlipVerticalMenu.Header = WelcomeText("flipv");
        ViewFrameMenu.Header = WelcomeText("frame");
        ViewFollowPlayerMenuItem.Header = WelcomeText("follow");
        DiagnosticsMenuItem.Header = WelcomeText("diagnostics");
        AssetsMenu.Header = WelcomeText("assets");
        BrowseAdoFaiMenuItem.Header = WelcomeText("browse");
        SetupAssetsMenuItem.Header = WelcomeText("setup");
        AboutMenuItem.Header = WelcomeText("about");
        ToolbarOpenButton.Content = WelcomeText("openmenu");
        ToolbarSaveButton.Content = WelcomeText("save");
        ToolbarFrameButton.Content = WelcomeText("frame");
        ToolbarStopButton.Content = WelcomeText("stop");
        ToolbarSpeedLabel.Text = WelcomeText("speed");
        FollowPlayerToggle.Content = WelcomeText("follow");
        HitSoundsToggle.Content = WelcomeText("hitsounds");
    }
}
