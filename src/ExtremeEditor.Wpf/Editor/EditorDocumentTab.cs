using System.IO;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

/// <summary>
/// Per-document identity and editor history for the future multi-tab workspace.
/// For now MainWindow owns one active tab; renderer/audio ownership is unchanged.
/// </summary>
internal sealed class EditorDocumentTab
{
    internal EditorDocumentTab(LevelDocument document, EditorSession editor)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(editor);
        if (!ReferenceEquals(editor.Document, document))
            throw new ArgumentException("Editor session must belong to the same document.", nameof(editor));

        Document = document;
        Editor = editor;
    }

    internal LevelDocument Document { get; }
    internal EditorSession Editor { get; }
    internal bool IsDirty => Editor.IsDirty;

    internal string DisplayName
    {
        get
        {
            string? path = Document.SourcePath;
            return string.IsNullOrWhiteSpace(path) || path == "<synthetic>"
                ? "Untitled.adofai"
                : Path.GetFileName(path);
        }
    }

    internal string Header => IsDirty ? $"{DisplayName} *" : DisplayName;
}
