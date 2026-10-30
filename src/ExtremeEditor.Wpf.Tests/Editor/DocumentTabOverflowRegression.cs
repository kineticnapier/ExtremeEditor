using System.IO;
using System.Xml.Linq;

namespace ExtremeEditor.Wpf.Tests;

internal static class DocumentTabOverflowRegression
{
    internal static void Run()
    {
        XDocument xaml = XDocument.Load(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement strip = xaml.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "DocumentTabStrip");
        XElement dock = strip.Elements().Single();
        Require(dock.Name.LocalName == "DockPanel" &&
                (string?)dock.Attribute("LastChildFill") == "True",
            "The tab strip must constrain its scrollable content to remaining width.");

        XElement[] children = dock.Elements().ToArray();
        XElement add = children.Single(element =>
            (string?)element.Attribute(x + "Name") == "AddDocumentTabButton");
        XElement scroll = children.Single(element =>
            (string?)element.Attribute(x + "Name") == "DocumentTabsScrollViewer");
        XElement host = scroll.Elements().Single(element =>
            (string?)element.Attribute(x + "Name") == "DocumentTabsHost");

        Require((string?)add.Attribute("DockPanel.Dock") == "Right",
            "New tab must stay visible independently of tab-list overflow.");
        Require((string?)add.Attribute("AutomationProperties.Name") == "New chart tab",
            "The fixed new-tab button must have an accessible name.");
        Require(ReferenceEquals(children[^1], scroll) &&
                (string?)host.Attribute("Orientation") == "Horizontal",
            "The horizontal tab host must be the fill child of the DockPanel.");
        Require((string?)scroll.Attribute("HorizontalScrollBarVisibility") == "Hidden" &&
                (string?)scroll.Attribute("VerticalScrollBarVisibility") == "Disabled" &&
                (string?)scroll.Attribute("CanContentScroll") == "False",
            "Tab scrolling must preserve the compact 33px strip height.");
        Require((string?)scroll.Attribute("PreviewMouseWheel") ==
                    "DocumentTabsScrollViewerPreviewMouseWheel" &&
                (string?)scroll.Attribute("SizeChanged") ==
                    "DocumentTabsScrollViewerSizeChanged",
            "Mouse wheel and window resize must keep overflow navigation working.");

        foreach ((string handler, string name) in new[] {
            ("ScrollDocumentTabsLeftClick", "Scroll chart tabs left"),
            ("ScrollDocumentTabsRightClick", "Scroll chart tabs right") })
        {
            Require(children.Any(element => (string?)element.Attribute("Click") == handler &&
                (string?)element.Attribute("DockPanel.Dock") == "Right" &&
                (string?)element.Attribute("AutomationProperties.Name") == name),
                "Both scroll buttons must remain visible and accessible outside the scroll viewport.");
        }

        string code = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.DocumentTabs.cs"));
        Require(code.Contains("visual.Chrome.BringIntoView();", StringComparison.Ordinal) &&
                code.Contains("_documentTabs.ActiveTab is EditorDocumentTab tab", StringComparison.Ordinal) &&
                code.Contains("DispatcherPriority.Loaded", StringComparison.Ordinal),
            "After asynchronous tab activation, scroll the current tab into view after layout.");
        Require(code.Contains("ScrollToHorizontalOffset(offset)", StringComparison.Ordinal) &&
                code.Contains("DocumentTabsScrollViewer.ScrollableWidth", StringComparison.Ordinal),
            "Scroll controls must clamp their offsets to the actual content extent.");
        Require(code.Contains("QueueActiveDocumentTabIntoView();", StringComparison.Ordinal),
            "Tab selection and resize must schedule active-tab visibility updates.");
    }

    private static string FindSourceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"RED setup failure: {relativePath} not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }
}
