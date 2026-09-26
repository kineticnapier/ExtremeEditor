using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class EditorSessionEventDeletionRegression
{
    public static void Run()
    {
        LevelDocument document = LevelDocument.CreateSynthetic(4);
        var session = new EditorSession(document);

        session.AddAction(1, "SetSpeed");
        LevelAction staleOriginal = session.GetActionsAtFloor(1).Single();
        int sourceIndex = staleOriginal.SourceIndex;

        LevelAction updated = staleOriginal with
        {
            Active = false,
            BeatsPerMinute = 180.0
        };
        session.ReplaceAction(staleOriginal, updated);

        LevelAction current = session.GetActionsAtFloor(1).Single();
        if (ReferenceEquals(staleOriginal, current))
            throw new InvalidOperationException("RED setup failure: ReplaceAction must produce a replacement action instance.");

        session.DeleteAction(staleOriginal);
        int remaining = session.Document.ActionCount;
        if (sourceIndex == -1 || remaining != 0)
        {
            throw new InvalidOperationException(
                $"RED: a newly added action must have a non-sentinel source identity and remain deletable through a stale pre-replace instance (SourceIndex={sourceIndex}, remaining={remaining}).");
        }

        session.Undo();
        LevelAction restored = session.GetActionsAtFloor(1).Single();
        if (restored.SourceIndex != sourceIndex || restored.Active)
            throw new InvalidOperationException("Undo delete must restore the updated action with the same source identity.");

        session.Redo();
        if (session.Document.ActionCount != 0)
            throw new InvalidOperationException("Redo delete must remove the restored action again.");
    }
}
