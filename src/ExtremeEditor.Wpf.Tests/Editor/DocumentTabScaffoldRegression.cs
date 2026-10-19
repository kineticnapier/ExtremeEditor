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

        var other = new LevelDocumentTabFixture();
        Require(!ReferenceEquals(tab.Document, other.Document) &&
                !ReferenceEquals(tab.Editor, other.Tab.Editor),
            "Document and edit history must not be shared across tab models.");

        string root = FindRepositoryRoot();
        string markup = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow.xaml"));
        XDocument xaml = XDocument.Parse(markup);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement? tabStrip = xaml.Descendants(ns + "Border")
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "DocumentTabStrip");
        XElement? title = xaml.Descendants(ns + "TextBlock")
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "ActiveDocumentTabTitle");
        Require(tabStrip is not null && title is not null &&
                (string?)tabStrip.Attribute("Visibility") ==
                    "{Binding Visibility, ElementName=ToolbarHost}",
            "Tab strip is visible during editing and hidden during Clean Play.");
        Require(markup.IndexOf("DocumentTabStrip", StringComparison.Ordinal) <
                markup.IndexOf("EditorShellGrid", StringComparison.Ordinal),
            "Document tabs must be displayed above the editor viewport.");

        string source = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.Editor.cs"));
        Require(source.Contains("_editor.Changed += ActiveEditorSessionChanged", StringComparison.Ordinal) &&
                source.Contains("_editor.Changed -= ActiveEditorSessionChanged", StringComparison.Ordinal),
            "Current editor notifications are swapped along with the active document.");
        Require(source.Contains("RefreshActiveDocumentTab();", StringComparison.Ordinal),
            "A new or saved editor updates the tab header.");
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
