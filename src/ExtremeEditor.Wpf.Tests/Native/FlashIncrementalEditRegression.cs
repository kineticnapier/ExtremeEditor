using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

// A Flash edit affects only the Flash timeline and, at most, the icon of the
// Flash floor. Adding, editing, deleting, undoing or redoing one must not
// rebuild the native snapshot, the playback setup or the managed geometry.
internal static class FlashIncrementalEditRegression
{
    public static void Run()
    {
        VerifyPlanClassification();
        VerifyWindowRoutesRecordedActionChanges();
        WithIconFixtures(() =>
        {
            VerifyFlashEditsMatchFullRebuild();
            VerifyMissingIconAssetFallsBackToFullLevel();
            VerifyLargeLevelFlashEditDoesNotScaleWithFloors();
        });
    }

    private static void VerifyPlanClassification()
    {
        LevelAction flash = CreateFlash(floor: 4, sourceIndex: 7, startOpacity: 100);
        LevelAction edited = flash with { PropertyOverrides = FlashProperties(4, 30) };

        AssertPlan("replace", flash, edited, NativeActionEditKind.FlashTimeline, 4);
        AssertPlan("add", null, flash, NativeActionEditKind.FlashTimeline, 4);
        AssertPlan("delete", flash, null, NativeActionEditKind.FlashTimeline, 4);
        AssertPlan("active toggle", flash, flash with { Active = false }, NativeActionEditKind.FlashTimeline, 4);

        AssertPlan("floor change", flash, edited with { Floor = 5 }, NativeActionEditKind.FullLevel);
        AssertPlan("source change", flash, edited with { SourceIndex = 8 }, NativeActionEditKind.FullLevel);
        AssertPlan(
            "type change",
            flash,
            flash with { EventType = "Bloom" },
            NativeActionEditKind.FullLevel);
        AssertPlan("twirl add", null, CreateTwirl(4, 9), NativeActionEditKind.FullLevel);
        AssertPlan("no change", null, null, NativeActionEditKind.FullLevel);
    }

    private static void AssertPlan(
        string scenario,
        LevelAction? before,
        LevelAction? after,
        NativeActionEditKind expected,
        int expectedFloor = -1)
    {
        NativeActionEditPlan plan = NativeActionEditUpdate.CreatePlan(before, after);
        if (plan.Kind != expected || (expectedFloor >= 0 && plan.StartFloor != expectedFloor))
        {
            throw new InvalidOperationException(
                $"Flash plan '{scenario}' is invalid: kind={plan.Kind}, floor={plan.StartFloor}, " +
                $"expected={expected}/{expectedFloor}.");
        }
    }

    // MainWindow cannot be created in this harness. Check that every event
    // edit entry point hands the recorded change to the refresh, and that the
    // Flash route skips the floor-sized playback setup.
    private static void VerifyWindowRoutesRecordedActionChanges()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "src", "ExtremeEditor.Wpf", "MainWindow");
        string editor = File.ReadAllText(Path.Combine(root, "MainWindow.Editor.cs"));
        string quickPicker = File.ReadAllText(Path.Combine(root, "MainWindow.EventQuickPicker.cs"));
        string autoApply = File.ReadAllText(Path.Combine(root, "MainWindow.EventAutoApply.cs"));

        foreach (string method in new[]
                 {
                     "ExecuteUndo", "ExecuteRedo", "ExecuteAddEvent", "ExecuteDeleteEvent", "ExecuteApplyEvent"
                 })
        {
            if (!MethodBody(editor, method).Contains("TakeLastActionChange()", StringComparison.Ordinal))
                throw new InvalidOperationException($"{method} does not pass the recorded action change.");
        }
        if (!MethodBody(quickPicker, "TryAddEventAtSelection").Contains("TakeLastActionChange()", StringComparison.Ordinal))
            throw new InvalidOperationException("Quick-picker event addition does not pass the recorded action change.");
        if (!MethodBody(autoApply, "RefreshAfterEventPropertyCommit").Contains("NativeActionEditKind.FlashTimeline", StringComparison.Ordinal))
            throw new InvalidOperationException("Event property commits do not use the Flash timeline route.");

        string refresh = MethodBody(editor, "RefreshEditorAfterMutation");
        int flashGate = refresh.IndexOf("NativeActionEditKind.FlashTimeline", StringComparison.Ordinal);
        int playbackSetup = refresh.IndexOf("WpfPlaybackSetupBuilder.Build(", StringComparison.Ordinal);
        if (flashGate < 0 || playbackSetup < 0 || flashGate > playbackSetup)
            throw new InvalidOperationException("RefreshEditorAfterMutation rebuilds playback setup for Flash edits.");
    }

    private static string MethodBody(string source, string method)
    {
        int start = source.IndexOf("void " + method + "(", StringComparison.Ordinal);
        if (start < 0)
            start = source.IndexOf("bool " + method + "(", StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"Method {method} was not found.");
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }
        throw new InvalidOperationException($"Method {method} body is unterminated.");
    }

    private static void VerifyFlashEditsMatchFullRebuild()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(64);
        LevelAction repeat = new(5, "RepeatEvents", true, null, null, null, null)
        {
            SourceIndex = 0,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = 5,
                ["eventType"] = "RepeatEvents",
                ["repeatType"] = "Floor",
                ["floorCount"] = 3,
                ["tag"] = "fx"
            }
        };
        LevelAction existingFlash = CreateFlash(5, sourceIndex: 1, startOpacity: 80);
        existingFlash.PropertyOverrides!["eventTag"] = "fx";
        level.ReplaceActions(
        [
            CreateTwirl(2, sourceIndex: 2),
            repeat,
            existingFlash,
            new LevelAction(7, "Checkpoint", true, null, null, null, null) { SourceIndex = 3 },
            CreateTwirl(9, sourceIndex: 4),
            // RepeatEvents owns floor 5's icon. This Flash uploads the Flash
            // icon asset that the incremental path must reuse.
            CreateFlash(20, sourceIndex: 5, startOpacity: 60)
        ]);

        var editor = new EditorSession(level);
        using var viewport = new NativeLevelViewport();
        viewport.SetPreparedLevel(level, NativeLevelViewport.PrepareLevel(level));
        TimingMap timing = FlatTimingMapBuilder.Build(level);
        viewport.SetPreparedPlayback(NativeLevelViewport.PreparePlayback(level, timing));
        NativeLevelSnapshot snapshot = viewport.SnapshotForDiagnostics
            ?? throw new InvalidOperationException("Initial native snapshot is missing.");
        PreparedNativePlayback baseline = NativeLevelViewport.PreparePlayback(level, timing);
        HitSoundTimeline baselineHitSounds = HitSoundTimelineBuilder.Build(level);

        void Step(string name, Action mutate)
        {
            RunFlashStep(name, editor, viewport, level, timing, mutate);
            if (!ReferenceEquals(snapshot, viewport.SnapshotForDiagnostics))
                throw new InvalidOperationException($"Flash step '{name}' replaced the native snapshot.");
            AssertMatchesFullRebuild(name, viewport, level, timing, baseline, baselineHitSounds);
        }

        Step("add on empty floor", () => editor.AddAction(12, "Flash"));
        Step("add on twirl floor", () => editor.AddAction(2, "Flash"));
        Step("add on terminal floor", () => editor.AddAction(level.FloorCount - 1, "Flash"));
        Step("edit", () =>
        {
            LevelAction added = FindFlash(level, 12);
            editor.ReplaceAction(added, added with
            {
                Duration = 2,
                PropertyOverrides = FlashProperties(12, 35, plane: "Foreground")
            });
        });
        Step("edit tagged", () =>
        {
            LevelAction tagged = FindFlash(level, 5);
            JsonObject properties = FlashProperties(5, 20);
            properties["eventTag"] = "fx";
            editor.ReplaceAction(tagged, tagged with { PropertyOverrides = properties });
        });
        Step("disable", () =>
        {
            LevelAction twirlFloorFlash = FindFlash(level, 2);
            editor.ReplaceAction(twirlFloorFlash, twirlFloorFlash with { Active = false });
        });
        Step("delete", () => editor.DeleteAction(FindFlash(level, 12)));
        for (int i = 0; i < 7; i++)
            Step($"undo {i + 1}", editor.Undo);
        for (int i = 0; i < 7; i++)
            Step($"redo {i + 1}", editor.Redo);
    }

    private static void RunFlashStep(
        string name,
        EditorSession editor,
        NativeLevelViewport viewport,
        LevelDocument level,
        TimingMap timing,
        Action mutate)
    {
        NativeActionEditKind kind;
        NativeLevelUpdateDiagnosticCounts counts;
        long geometryRebuilds;
        NativeLevelUpdateDiagnostics.EnableAndReset();
        LevelGeometryDiagnostics.EnableAndReset();
        try
        {
            mutate();
            geometryRebuilds = LevelGeometryDiagnostics.ReadRebuildCount();
            EditorActionChange change = editor.TakeLastActionChange()
                ?? throw new InvalidOperationException(
                    $"Flash step '{name}' did not record the edited action.");
            kind = NativeActionEditUpdate.Apply(viewport, level, change.Before, change.After, timing);
            counts = NativeLevelUpdateDiagnostics.Read();
        }
        finally
        {
            LevelGeometryDiagnostics.Disable();
            NativeLevelUpdateDiagnostics.Disable();
        }

        if (kind != NativeActionEditKind.FlashTimeline ||
            counts.FullLevelReplacementRequests != 0 ||
            counts.PrepareLevelCalls != 0 ||
            counts.FlatSnapshotBuilds != 0 ||
            counts.PlaybackSetupBuilds != 0 ||
            counts.FlashTimelineUpdateRequests != 1 ||
            geometryRebuilds != 0)
        {
            throw new InvalidOperationException(
                $"Flash step '{name}' rebuilt floor-sized state: kind={kind}, " +
                $"replacements={counts.FullLevelReplacementRequests}, " +
                $"prepareLevel={counts.PrepareLevelCalls}, " +
                $"flatSnapshotBuilds={counts.FlatSnapshotBuilds}, " +
                $"playbackSetupBuilds={counts.PlaybackSetupBuilds}, " +
                $"flashUpdates={counts.FlashTimelineUpdateRequests}, " +
                $"geometryRebuilds={geometryRebuilds}.");
        }
    }

    private static void AssertMatchesFullRebuild(
        string step,
        NativeLevelViewport viewport,
        LevelDocument level,
        TimingMap timing,
        PreparedNativePlayback baseline,
        HitSoundTimeline baselineHitSounds)
    {
        NativeLevelSnapshot actual = viewport.SnapshotForDiagnostics!;
        NativeLevelSnapshot oracle = FlatNativeLevelSnapshotBuilder.BuildProfiled(level).Snapshot;
        for (int floor = 0; floor < level.FloorCount; floor++)
        {
            string? actualPath = GetIconPath(actual, floor);
            string? expectedPath = GetIconPath(oracle, floor);
            NativeFloor a = actual.Floors[floor];
            NativeFloor e = oracle.Floors[floor];
            if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
                a.IconFlags != e.IconFlags ||
                MathF.Abs(a.IconAngle - e.IconAngle) > 0.000001f ||
                actual.CcwBeforeFloor[floor] != oracle.CcwBeforeFloor[floor])
            {
                throw new InvalidOperationException(
                    $"Flash step '{step}' floor {floor} icon differs from a full rebuild: " +
                    $"actual=({actualPath}, {a.IconFlags}, {a.IconAngle}), " +
                    $"expected=({expectedPath}, {e.IconFlags}, {e.IconAngle}).");
            }
        }

        TimingMap oracleTiming = FlatTimingMapBuilder.Build(level);
        for (int floor = 0; floor < level.FloorCount; floor++)
        {
            if (!oracleTiming.Floors[floor].Equals(timing.Floors[floor]))
                throw new InvalidOperationException($"Flash step '{step}' changed timing at floor {floor}.");
        }

        if (!HitSoundTimelineBuilder.Build(level).Changes.SequenceEqual(baselineHitSounds.Changes))
            throw new InvalidOperationException($"Flash step '{step}' changed the hit-sound timeline.");

        PreparedNativePlayback expected = NativeLevelViewport.PreparePlayback(level, oracleTiming);
        AssertSame(step, "flash", viewport.FlashTimelineForDiagnostics, expected.FlashTimeline);
        AssertSame(step, "playback", expected.PlaybackTimeline, baseline.PlaybackTimeline);
        AssertSame(step, "camera", expected.CameraTimeline, baseline.CameraTimeline);
        AssertSame(step, "bloom", expected.BloomTimeline, baseline.BloomTimeline);
        AssertSame(step, "screenTile", expected.ScreenTileTimeline, baseline.ScreenTileTimeline);
        AssertSame(step, "filter", expected.FilterTimeline, baseline.FilterTimeline);
        AssertSame(step, "background", expected.BackgroundTimeline, baseline.BackgroundTimeline);
        AssertSame(step, "trackTransform", expected.TrackTransformTimeline, baseline.TrackTransformTimeline);
        AssertSame(step, "trackVisual", expected.TrackVisualTimeline, baseline.TrackVisualTimeline);
        AssertSame(step, "trackAnimation", expected.TrackAnimationTimeline, baseline.TrackAnimationTimeline);
        AssertSame(step, "trackTimings", expected.TrackAnimationTimings, baseline.TrackAnimationTimings);
    }

    private static void AssertSame(string step, string timeline, Array actual, Array expected)
    {
        if (!StructuralComparisons.StructuralEqualityComparer.Equals(actual, expected))
        {
            throw new InvalidOperationException(
                $"Flash step '{step}' {timeline} timeline differs from a full rebuild " +
                $"(actual={actual.Length}, expected={expected.Length}).");
        }
    }

    private static void VerifyMissingIconAssetFallsBackToFullLevel()
    {
        // The first Flash in a chart needs an icon asset the uploaded scene
        // does not have yet. That case must keep the full replacement.
        LevelDocument level = LevelDocument.CreateSynthetic(16);
        var editor = new EditorSession(level);
        using var viewport = new NativeLevelViewport();
        viewport.SetPreparedLevel(level, NativeLevelViewport.PrepareLevel(level));
        TimingMap timing = FlatTimingMapBuilder.Build(level);
        viewport.SetPreparedPlayback(NativeLevelViewport.PreparePlayback(level, timing));

        editor.AddAction(3, "Flash");
        EditorActionChange change = editor.TakeLastActionChange()
            ?? throw new InvalidOperationException("First Flash addition did not record the action.");
        NativeLevelUpdateDiagnostics.EnableAndReset();
        NativeActionEditKind kind;
        NativeLevelUpdateDiagnosticCounts counts;
        try
        {
            kind = NativeActionEditUpdate.Apply(viewport, level, change.Before, change.After, timing);
            counts = NativeLevelUpdateDiagnostics.Read();
        }
        finally
        {
            NativeLevelUpdateDiagnostics.Disable();
        }

        if (kind != NativeActionEditKind.FullLevel || counts.FullLevelReplacementRequests != 1)
        {
            throw new InvalidOperationException(
                $"First Flash without an uploaded icon asset must fall back: kind={kind}, " +
                $"replacements={counts.FullLevelReplacementRequests}.");
        }
        if (counts.FlashTimelineUpdateRequests != 0)
            throw new InvalidOperationException("Flash fallback also applied a partial Flash update.");
    }

    private static void VerifyLargeLevelFlashEditDoesNotScaleWithFloors()
    {
        const int floors = 2_000_000;
        LevelDocument level = LevelDocument.CreateSynthetic(floors);
        level.ReplaceActions([CreateFlash(10, sourceIndex: 0, startOpacity: 100)]);
        var editor = new EditorSession(level);
        using var viewport = new NativeLevelViewport();
        var snapshotWatch = Stopwatch.StartNew();
        viewport.SetPreparedLevel(level, NativeLevelViewport.PrepareLevel(level));
        snapshotWatch.Stop();
        var setupWatch = Stopwatch.StartNew();
        TimingMap timing = WpfPlaybackSetupBuilder.Build(level).TimingMap;
        setupWatch.Stop();

        var fullWatch = Stopwatch.StartNew();
        viewport.SetPreparedPlayback(NativeLevelViewport.PreparePlayback(level, timing));
        fullWatch.Stop();

        var editWatch = Stopwatch.StartNew();
        RunFlashStep("large add", editor, viewport, level, timing,
            () => editor.AddAction(floors / 2, "Flash"));
        RunFlashStep("large edit", editor, viewport, level, timing, () =>
        {
            LevelAction added = FindFlash(level, floors / 2);
            editor.ReplaceAction(added, added with { PropertyOverrides = FlashProperties(floors / 2, 10) });
        });
        RunFlashStep("large delete", editor, viewport, level, timing,
            () => editor.DeleteAction(FindFlash(level, floors / 2)));
        RunFlashStep("large undo", editor, viewport, level, timing, editor.Undo);
        editWatch.Stop();

        Console.WriteLine(
            $"Flash edit on {floors:N0} floors: 4 edits={editWatch.Elapsed.TotalMilliseconds:F1}ms, " +
            $"replaced per-edit work: snapshot={snapshotWatch.Elapsed.TotalMilliseconds:F1}ms, " +
            $"playback setup={setupWatch.Elapsed.TotalMilliseconds:F1}ms, " +
            $"playback timelines={fullWatch.Elapsed.TotalMilliseconds:F1}ms.");
        // Counters above are the primary check. The timing check only guards
        // against a floor-sized pass sneaking back into the Flash path.
        if (editWatch.Elapsed > fullWatch.Elapsed)
        {
            throw new InvalidOperationException(
                $"Four Flash edits took longer than one playback preparation: " +
                $"{editWatch.Elapsed.TotalMilliseconds:F1}ms > {fullWatch.Elapsed.TotalMilliseconds:F1}ms.");
        }
    }

    private static void WithIconFixtures(Action test)
    {
        string[] paths =
        [
            IconAssetCache.FloorPath("SwirlRed"),
            IconAssetCache.FloorPath("SwirlBlue"),
            IconAssetCache.FloorPath("Checkpoint"),
            IconAssetCache.FloorPath("Portal"),
            IconAssetCache.EventPath("Flash"),
            IconAssetCache.EventPath("RepeatEvents")
        ];
        var backups = paths.ToDictionary(
            path => path,
            path => File.Exists(path) ? File.ReadAllBytes(path) : null,
            StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (string path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [0x46, 0x4C, 0x41, 0x53, 0x48]);
            }
            test();
        }
        finally
        {
            foreach ((string path, byte[]? backup) in backups)
            {
                if (backup is null)
                    File.Delete(path);
                else
                    File.WriteAllBytes(path, backup);
            }
        }
    }

    private static LevelAction FindFlash(LevelDocument level, int floor) =>
        level.ActionStore.Actions.Last(action =>
            action.Floor == floor && string.Equals(action.EventType, "Flash", StringComparison.Ordinal));

    private static LevelAction CreateFlash(int floor, int sourceIndex, double startOpacity) =>
        new(floor, "Flash", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            Duration = 1,
            PropertyOverrides = FlashProperties(floor, startOpacity)
        };

    private static JsonObject FlashProperties(int floor, double startOpacity, string plane = "Background") => new()
    {
        ["floor"] = floor,
        ["eventType"] = "Flash",
        ["startColor"] = "ff0000",
        ["endColor"] = "00ff00",
        ["startOpacity"] = startOpacity,
        ["endOpacity"] = 0,
        ["plane"] = plane,
        ["ease"] = "OutQuad"
    };

    private static LevelAction CreateTwirl(int floor, int sourceIndex) =>
        new(floor, "Twirl", true, null, null, null, null)
        {
            Kind = LevelActionKind.Twirl,
            SourceIndex = sourceIndex
        };

    private static string? GetIconPath(NativeLevelSnapshot snapshot, int floor)
    {
        uint iconId = snapshot.Floors[floor].IconId;
        return iconId == NativeFloor.NoIcon || iconId >= snapshot.IconAssets.Length
            ? null
            : snapshot.IconAssets[iconId].ImagePath;
    }
}
