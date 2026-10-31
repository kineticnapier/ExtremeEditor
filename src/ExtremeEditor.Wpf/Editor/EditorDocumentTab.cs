using System.IO;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

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

    // A tab retains its own transport and selection when another document is
    // active. The native viewport remains shared for this first iteration.
    internal AudioPlayer? Audio { get; set; }
    internal TimingMap? TimingMap { get; set; }
    internal HitSoundTimeline? HitSoundTimeline { get; set; }
    internal int AudioPreparationGeneration { get; set; }
    internal bool AudioPreparationPending { get; set; }
    internal int[] SelectedFloors { get; set; } = [0];
    internal int PrimaryFloor { get; set; } = 0;
    internal int? SelectedEventSourceIndex { get; set; }
    internal NativeEditorCameraState? EditorCamera { get; set; }
    internal Dictionary<int, string> PendingRawEventJson { get; } = [];

    internal bool IsDirty => Editor.IsDirty || PendingRawEventJson.Count > 0;

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
