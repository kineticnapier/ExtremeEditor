using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class IncrementalNativeEditRegression
{
    public static void Run()
    {
        VerifyAudioTimelineStillUpdates();

        VerifyFastPath(
            "hitsound and volume",
            action => action with
            {
                HitSound = "Sizzle",
                HitSoundVolumePercent = 70
            });
        VerifyFastPath(
            "volume only",
            action => action with { HitSoundVolumePercent = 45 });
        VerifyFastPath(
            "gameSound only",
            action => action with { GameSound = "Midspin" });

        VerifyFullReplacement(
            "active",
            action => action with { Active = false });
        VerifyFullReplacement(
            "eventType",
            action => action with
            {
                EventType = "Twirl",
                Kind = LevelActionKind.Twirl
            });
        VerifyFullReplacement(
            "floor",
            action => action with { Floor = action.Floor + 1 });
        VerifyFullReplacement(
            "source identity",
            action => action with { SourceIndex = action.SourceIndex + 1 },
            replaceThroughEditor: false);
    }

    private static void VerifyAudioTimelineStillUpdates()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        LevelAction before = CreateSetHitsound();
        level.ReplaceActions([before]);

        var editor = new EditorSession(level);
        editor.ReplaceAction(
            before,
            before with
            {
                HitSound = "Sizzle",
                HitSoundVolumePercent = 70
            });

        HitSoundState state = HitSoundTimelineBuilder.Build(level).GetStateAtFloor(before.Floor);
        if (!string.Equals(state.Name, "Sizzle", StringComparison.Ordinal) ||
            Math.Abs(state.Volume - 0.7) > 0.000001)
        {
            throw new InvalidOperationException(
                $"SetHitsound fast path lost the audio timeline update: {state.Name} {state.Volume}.");
        }
    }

    private static void VerifyFastPath(string scenario, Func<LevelAction, LevelAction> edit)
    {
        NativeLevelUpdateDiagnosticCounts counts = RunEdit(edit, replaceThroughEditor: true);
        if (counts.FullLevelReplacementRequests != 0 ||
            counts.PrepareLevelCalls != 0 ||
            counts.FlatSnapshotBuilds != 0)
        {
            throw new InvalidOperationException(
                "metadata-only action edit triggered full native snapshot rebuild. " +
                $"scenario={scenario}, " +
                $"replacements={counts.FullLevelReplacementRequests}, " +
                $"prepareLevel={counts.PrepareLevelCalls}, " +
                $"flatSnapshotBuilds={counts.FlatSnapshotBuilds}.");
        }
    }

    private static void VerifyFullReplacement(
        string scenario,
        Func<LevelAction, LevelAction> edit,
        bool replaceThroughEditor = true)
    {
        NativeLevelUpdateDiagnosticCounts counts = RunEdit(edit, replaceThroughEditor);
        if (counts.FullLevelReplacementRequests != 1 ||
            counts.PrepareLevelCalls != 0 ||
            counts.FlatSnapshotBuilds != 0)
        {
            throw new InvalidOperationException(
                $"SetHitsound {scenario} edit must retain full native replacement. " +
                $"replacements={counts.FullLevelReplacementRequests}, " +
                $"prepareLevel={counts.PrepareLevelCalls}, " +
                $"flatSnapshotBuilds={counts.FlatSnapshotBuilds}.");
        }
    }

    private static NativeLevelUpdateDiagnosticCounts RunEdit(
        Func<LevelAction, LevelAction> edit,
        bool replaceThroughEditor)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        LevelAction before = CreateSetHitsound();
        level.ReplaceActions([before]);

        var editor = new EditorSession(level);
        using var viewport = new NativeLevelViewport();

        PreparedNativeLevel initial = NativeLevelViewport.PrepareLevel(level);
        viewport.SetPreparedLevel(level, initial);

        LevelAction after = edit(before);
        if (replaceThroughEditor)
        {
            editor.ReplaceAction(before, after);
            after = editor.Document.ActionStore.Actions
                .Single(action => action.SourceIndex == before.SourceIndex);
        }

        NativeLevelUpdateDiagnostics.EnableAndReset();
        try
        {
            NativeActionEditUpdate.Apply(viewport, level, before, after);
            return NativeLevelUpdateDiagnostics.Read();
        }
        finally
        {
            NativeLevelUpdateDiagnostics.Disable();
        }
    }

    private static LevelAction CreateSetHitsound() =>
        new(3, "SetHitsound", true, null, null, null, null)
        {
            SourceIndex = 0,
            GameSound = "Hitsound",
            HitSound = "Kick",
            HitSoundVolumePercent = 100
        };
}
