using System.IO;
using System.Xml.Linq;

namespace ExtremeEditor.Wpf.Tests;

/// <summary>
/// Source-XAML/UI-copy RED contract for the Japanese-first Together workflow.
/// Does not instantiate MainWindow or automate clicks. Runs against the checked
/// out WPF markup, so every visible label must be intentional, not just a
/// translated design document.
/// </summary>
internal static class TogetherJapaneseUiRegression
{
    public static void Run()
    {
        string root = FindRepositoryRoot();
        string dialogFile = Path.Combine(root, "src", "ExtremeEditor.Wpf",
            "Dialogs", "TogetherWorkspaceWindow.xaml");
        string codeFile = Path.ChangeExtension(dialogFile, ".xaml.cs");
        string mainFile = Path.Combine(root, "src", "ExtremeEditor.Wpf", "MainWindow.xaml");

        XDocument dialog = XDocument.Load(dialogFile);
        XDocument main = XDocument.Load(mainFile);
        XElement window = dialog.Root ??
            throw new InvalidOperationException("RED: Together XAML window is missing.");

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string title = (string?)window.Attribute("Title") ?? string.Empty;
        Check(title.Contains("共同編集", StringComparison.Ordinal),
            "Togetherウィンドウのタイトルを日本語にする");

        XElement create = RequiredNamed(dialog, x, "CreateProjectButton");
        XElement join = RequiredNamed(dialog, x, "JoinProjectButton");
        XElement overview = RequiredNamed(dialog, x, "ProjectOverviewText");
        XElement shared = RequiredNamed(dialog, x, "ShareChangesButton");
        XElement export = RequiredNamed(dialog, x, "ExportButton");
        XElement advanced = RequiredNamed(dialog, x, "AdvancedControlsExpander");
        XElement segments = RequiredNamed(dialog, x, "SegmentList");
        XElement status = RequiredNamed(dialog, x, "TogetherStatusText");

        Check(((string?)create.Attribute("Content"))?.Contains("作成") == true,
            "新しい共同編集を作成する操作が日本語であること");
        Check(((string?)join.Attribute("Content"))?.Contains("参加") == true,
            "既存の共同編集に参加する操作が日本語であること");
        Check(((string?)shared.Attribute("Content"))?.Contains("共有") == true,
            "同期の一連の操作を『変更を共有』と表示すること");
        Check(((string?)export.Attribute("Content"))?.Contains("書き出") == true,
            "完成譜面の書き出しを日本語で表示すること");
        Check(((string?)advanced.Attribute("Header"))?.Contains("詳細") == true &&
              (string?)advanced.Attribute("IsExpanded") == "False",
            "Git/サンプル周波数/分割数の専門設定は折りたたんで配置すること");
        Check(advanced.Descendants().Any(element =>
                (string?)element.Attribute(x + "Name") == "SampleRateBox"),
            "サンプル周波数は詳細設定内で明示・編集できること");
        Check((string?)segments.Attribute("x:Name") is null,
            "XAMLは名前空間を尊重してパースされていること");
        Check(((string?)status.Attribute("Text"))?.Contains("共同編集") == true,
            "初期状態の案内を日本語にすること");
        Check(((string?)overview.Attribute("Text"))?.Contains("譜面") == true,
            "操作前に、現在の譜面が対象であることを説明すること");

        XElement menu = RequiredNamed(main, x, "TogetherMenuItem");
        Check(((string?)menu.Attribute("Header"))?.Contains("共同編集") == true,
            "メニューバーのTogether入口も日本語にすること");

        string visualCopy = string.Join("\n", dialog.DescendantsAndSelf()
            .SelectMany(e => e.Attributes())
            .Where(attribute => attribute.Name.LocalName is
                "Title" or "Header" or "Content" or "Text" or "ToolTip")
            .Select(attribute => attribute.Value));

        string[] oldVisibleEnglish =
        [
            "Local workspace", "My owner ID", "Sample rate", "Cut floors",
            "Owners", "Local bare Git collaboration", "Start floor",
            "End floor", "Start sample", "End sample", "Writable",
            "Stage selected Segment", "Export published .adofai",
            "No Together workspace opened."
        ];
        foreach (string english in oldVisibleEnglish)
            Check(!visualCopy.Contains(english, StringComparison.Ordinal),
                "利用者向けの英語ラベルが残っている: " + english);

        string code = File.ReadAllText(codeFile);
        Check(code.Contains("ShareChangesClick(", StringComparison.Ordinal),
            "『変更を共有』には処理が結び付いていること");
        foreach (string obsolete in new[]
        {
            "Open an .adofai chart first;",
            "Create or open an isolated local Together Git workspace",
            "No Together workspace opened."
        })
            Check(!code.Contains(obsolete, StringComparison.Ordinal),
                "状態メッセージは日本語にすること: " + obsolete);

        // This test is intentionally structural. Backend multi-client tests
        // remain separate and must still pass after the GUI work.
    }

    private static XElement RequiredNamed(XDocument document, XNamespace x, string id) =>
        document.Descendants()
            .SingleOrDefault(element => (string?)element.Attribute(x + "Name") == id)
        ?? throw new InvalidOperationException(
            "RED: 日本語優先の共同編集UIに必要な要素がない: " + id);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src",
                "ExtremeEditor.Wpf", "Dialogs", "TogetherWorkspaceWindow.xaml");
            if (File.Exists(candidate))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "REDテスト用にソースcheckoutから実行してください。WPF XAMLが見つかりません。");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }
}
