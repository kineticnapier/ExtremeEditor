using System.IO;
using System.Xml.Linq;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class DocumentTabScaffoldRegression
{
    internal static void Run()
    {
        LevelDocument document = LevelDocument.CreateSynthetic(2);
        var editor = new EditorSession(document);
        var tab = new EditorDocumentTab(document, editor);

        Require(tab.Document == document && tab.Editor == editor,
            "Tab owns the same document and editor session.");
        Require(tab.Header == "Untitled.adofai" && !tab.IsDirty,
            "New documents start with a clean Untitled tab.");

        editor.InsertAngle(0, 45.0);
        Require(tab.IsDirty && tab.Header == "Untitled.adofai *",
            "The same tab reports unsaved edits without changing editor sessions.");

        editor.Undo();
        Require(!tab.IsDirty && tab.Header == "Untitled.adofai",
            "Undo to the saved revision clears the dirty marker.");

        document.SourcePath = Path.Combine(Path.GetTempPath(), "multi-editor-test.adofai");
        Require(tab.Header == "multi-editor-test.adofai",
            "The header tracks the filename after the document acquires a path.");

        tab.PrimaryFloor = 1;
        tab.SelectedFloors = [1];
        tab.TimingMap = TimingMapBuilder.Build(document);
        Require(tab.PrimaryFloor == 1 && tab.SelectedFloors.SequenceEqual([1]) &&
                tab.TimingMap is not null,
            "Per-document tab retains its own selection and playback timing.");

        var other = new LevelDocumentTabFixture();
        Require(!ReferenceEquals(tab.Document, other.Document) &&
                !ReferenceEquals(tab.Editor, other.Tab.Editor),
            "Document and edit history must not be shared across tab models.");
        Require(other.Tab.TimingMap is null && other.Tab.PrimaryFloor == 0,
            "Inactive tab state is independent of the current chart.");

        VerifyTabCollection();


        string root = FindRepositoryRoot();
        string markup = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow.xaml"));
        XDocument xaml = XDocument.Parse(markup);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement? tabStrip = xaml.Descendants(ns + "Border")
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "DocumentTabStrip");
        XElement? tabsHost = xaml.Descendants(ns + "StackPanel")
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "DocumentTabsHost");
        Require(tabStrip is not null && tabsHost is not null &&
                (string?)tabStrip.Attribute("Visibility") ==
                    "{Binding Visibility, ElementName=ToolbarHost}",
            "Tab strip is visible during editing and hidden during Clean Play.");
        Require(markup.IndexOf("DocumentTabStrip", StringComparison.Ordinal) <
                markup.IndexOf("EditorShellGrid", StringComparison.Ordinal),
            "Document tabs must be displayed above the editor viewport.");
        Require(markup.Contains("x:Name=\"DocumentTabsHost\"", StringComparison.Ordinal) &&
                !markup.Contains("x:Name=\"ActiveDocumentTab\"", StringComparison.Ordinal) &&
                markup.Contains("x:Name=\"AddDocumentTabButton\"", StringComparison.Ordinal) &&
                markup.Contains("Click=\"NewDocumentTabClick\"", StringComparison.Ordinal),
            "A single ordered tab host and plus button can show every chart tab.");


        string source = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.Editor.cs"));
        Require(source.Contains("_editor.Changed += ActiveEditorSessionChanged", StringComparison.Ordinal) &&
                source.Contains("_editor.Changed -= ActiveEditorSessionChanged", StringComparison.Ordinal),
            "Current editor notifications are swapped along with the active document.");
        Require(source.Contains("RefreshActiveDocumentTab();", StringComparison.Ordinal),
            "A new or saved editor updates the tab header.");
        Require(source.Contains("_documentTabs.ResetToSingle(", StringComparison.Ordinal),
            "Legacy single-document Open uses the explicit compatibility bridge.");
        string controller = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.DocumentTabs.cs"));
        Require(controller.Contains("ActivateDocumentTab(", StringComparison.Ordinal) &&
                controller.Contains("previous.Audio = _audio", StringComparison.Ordinal) &&
                controller.Contains("previous.SelectedFloors = _selection.SelectedFloors.ToArray()", StringComparison.Ordinal) &&
                controller.Contains("NativeViewport.SetLevel(tab.Document)", StringComparison.Ordinal) &&
                controller.Contains("_editor?.Changed -= ActiveEditorSessionChanged", StringComparison.Ordinal),
            "Switching tabs preserves the previous audio, selection and editor session.");
        Require(controller.Contains("foreach (EditorDocumentTab tab in _documentTabs.Tabs)", StringComparison.Ordinal) &&
                controller.Contains("bool isActive = ReferenceEquals(tab, _documentTabs.ActiveTab)", StringComparison.Ordinal) &&
                controller.Contains("DocumentTabsHost.Children.Add(tabChrome)", StringComparison.Ordinal) &&
                !controller.Contains("if (ReferenceEquals(tab, active))", StringComparison.Ordinal),
            "The active tab is rendered in its original position alongside every other tab.");
        string keyboard = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.AdoFaiKeybinds.cs"));
        Require(keyboard.Contains("key == Key.N", StringComparison.Ordinal) &&
                keyboard.Contains("CreateNewDocumentTab();", StringComparison.Ordinal),
            "Ctrl+N reaches tab creation through the native HWND keyboard bridge.");
        string main = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow.xaml.cs"));
        Require(main.Contains("_documentTabs.FindByPath(dialog.FileName)", StringComparison.Ordinal) &&
                main.Contains("ActivateDocumentTab(existing)", StringComparison.Ordinal) &&
                !main.Contains("if (_documentTabs.Tabs.Count > 1)", StringComparison.Ordinal),
            "Open reuses an existing chart tab instead of replacing other tabs.");
        Require(source.Contains("Other chart tabs have unsaved changes.", StringComparison.Ordinal),
            "Closing the window protects modified inactive charts.");
        Require(source.Contains("_documentTabs.FindByPath(path!)", StringComparison.Ordinal),
            "Save As must not overwrite a different open tab.");
        string together = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.Together.cs"));
        Require(together.Contains("if (_documentTabs.Tabs.Count > 1)", StringComparison.Ordinal) &&
                together.Contains("_documentTabs.ResetToSingle(", StringComparison.Ordinal),
            "Together cannot replace multi-tab state without confirmation.");



    }

    private static void VerifyTabCollection()
    {
        var tabs = new EditorDocumentTabs();
        var firstDocument = LevelDocument.CreateSynthetic(2);
        string firstPath = Path.Combine(Path.GetTempPath(), "tab-one.adofai");
        firstDocument.SourcePath = firstPath;
        var firstEditor = new EditorSession(firstDocument);
        var first = new EditorDocumentTab(firstDocument, firstEditor);

        var secondDocument = LevelDocument.CreateSynthetic(2);
        secondDocument.SourcePath = Path.Combine(Path.GetTempPath(), "tab-two.adofai");
        var secondEditor = new EditorSession(secondDocument);
        var second = new EditorDocumentTab(secondDocument, secondEditor);

        var thirdDocument = LevelDocument.CreateSynthetic(2);
        var third = new EditorDocumentTab(thirdDocument, new EditorSession(thirdDocument));

        Require(tabs.ActiveTab is null && tabs.Tabs.Count == 0,
            "A fresh tab collection has no active chart.");
        Require(tabs.Add(first), "First chart can be registered.");
        Require(tabs.ActiveTab == first && tabs.Tabs.Count == 1,
            "The first chart automatically becomes active.");
        Require(tabs.Add(second, activate: false) &&
                tabs.ActiveTab == first && tabs.Tabs.Count == 2,
            "Background registration preserves the active tab.");
        Require(!tabs.Add(first) && tabs.Tabs.Count == 2,
            "A chart cannot be added twice by document identity.");

        var duplicateDocument = LevelDocument.CreateSynthetic(2);
        duplicateDocument.SourcePath = firstPath.ToUpperInvariant();
        var duplicate = new EditorDocumentTab(
            duplicateDocument, new EditorSession(duplicateDocument));
        Require(!tabs.Add(duplicate) && tabs.Tabs.Count == 2,
            "A different editor cannot open the same path in a second tab.");
        Require(tabs.FindByPath(firstPath) == first &&
                tabs.FindByPath(firstPath.ToUpperInvariant()) == first,
            "Existing tabs are found by case-insensitive normalized path.");

        firstEditor.InsertAngle(0, 90.0);
        Require(first.IsDirty && !second.IsDirty,
            "Editing one tab never dirties another editor.");
        Require(tabs.Add(third) && tabs.ActiveTab == third &&
                tabs.Tabs[0] == first && tabs.Tabs[1] == second &&
                tabs.Tabs[2] == third,
            "Adding a tab preserves ordering and may select it.");
        Require(tabs.Activate(second) && tabs.ActiveTab == second,
            "An existing tab can become active without recreating it.");
        Require(tabs.Tabs.SequenceEqual([first, second, third]),
            "Selecting a middle tab does not reorder the collection.");
        Require(tabs.Activate(first) && tabs.Tabs.SequenceEqual([first, second, third]),
            "Selecting the first tab leaves the original order intact.");
        Require(tabs.Activate(third) && tabs.Tabs.SequenceEqual([first, second, third]),
            "Selecting the final tab leaves the original order intact.");
        Require(tabs.Activate(second) && tabs.Tabs.SequenceEqual([first, second, third]),
            "Repeated switching preserves tab order.");
        Require(!tabs.Activate(duplicate) && tabs.ActiveTab == second,
            "An unregistered tab cannot be activated.");
        Require(tabs.Remove(second) && tabs.ActiveTab == third,
            "Closing the active middle tab selects the next one.");
        Require(tabs.Remove(third) && tabs.ActiveTab == first && first.IsDirty,
            "Closing the final tab selects the previous one and retains its history.");
        Require(tabs.Remove(first) && tabs.ActiveTab is null && tabs.Tabs.Count == 0,
            "Closing the last chart empties the collection.");
        Require(!tabs.Remove(first), "A closed chart cannot be closed twice.");

        Require(tabs.Add(first) && tabs.Add(second), "Tabs can be reopened.");
        tabs.ResetToSingle(third);
        Require(tabs.ActiveTab == third && tabs.Tabs.Count == 1 && tabs.Tabs[0] == third,
            "The legacy single-document Open path can replace its tab identity.");
    }

    private sealed class LevelDocumentTabFixture
    {
        internal LevelDocument Document { get; } = LevelDocument.CreateSynthetic(2);
        internal EditorDocumentTab Tab { get; }

        internal LevelDocumentTabFixture()
        {
            Tab = new EditorDocumentTab(Document, new EditorSession(Document));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExtremeEditor.sln")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Cannot locate ExtremeEditor repository root.");
    }
}
