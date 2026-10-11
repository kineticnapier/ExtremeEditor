using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class PositionTrackDragRegression
{
    internal static void Run()
    {
        MethodInfo create = typeof(EditorSession).GetMethod(
            "AddPositionTrackFromDrag",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(int), typeof(double), typeof(double)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "EditorSession.AddPositionTrackFromDrag(floor, offsetX, offsetY) is missing.");


        MethodInfo createRange = typeof(EditorSession).GetMethod(
            "AddPositionTrackRangeFromDrag",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(int), typeof(int), typeof(double), typeof(double)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "EditorSession.AddPositionTrackRangeFromDrag(firstFloor, lastFloor, offsetX, offsetY) is missing.");

        LevelDocument level = LevelDocument.CreateSynthetic(5);
        var editor = new EditorSession(level);
        var request = NativeRendererSession.PositionTrackDragRequest(2, 2, 1.875, -1.125);
        AssertNear(1.25, request.Value, "world to tile X");
        AssertNear(-0.75, request.ValueY, "world to tile Y");
        if (request.LastFloor != 2)
            throw new InvalidOperationException("native PositionTrack request lost the frozen last floor.");
        if (editor.AddPositionTrackFromDrag(-1, 1, 1) ||
            editor.AddPositionTrackFromDrag(5, 1, 1) ||
            editor.AddPositionTrackFromDrag(2, double.NaN, 1) ||
            editor.AddPositionTrackFromDrag(2, 1, double.PositiveInfinity) ||
            editor.AddPositionTrackFromDrag(2, 0, 0) || editor.CanUndo)
            throw new InvalidOperationException("invalid/empty drag must not create history.");
        object? result = create.Invoke(editor, [2, 1.25, -0.75]);
        if (result is bool created && !created)
            throw new InvalidOperationException("valid Alt-drag delta was rejected.");

        LevelAction action = level.ActionStore.Actions.SingleOrDefault(candidate =>
            candidate.Floor == 2 && candidate.EventType == "PositionTrack")
            ?? throw new InvalidOperationException("Alt-drag did not create one PositionTrack at the source floor.");
        JsonArray offset = (action.PropertyOverrides ?? action.SourceProperties)?["positionOffset"] as JsonArray
            ?? throw new InvalidOperationException("PositionTrack positionOffset is missing.");
        AssertNear(1.25, offset[0]?.GetValue<double>() ?? double.NaN, "offset X");
        AssertNear(-0.75, offset[1]?.GetValue<double>() ?? double.NaN, "offset Y");
        if (editor.UndoName != "Add PositionTrack")
            throw new InvalidOperationException("Alt-drag creation is not one named undo operation.");
        AssertResolved(level, 1.25, -0.75);

        editor.Undo();
        if (level.ActionStore.Actions.Any(candidate => candidate.EventType == "PositionTrack"))
            throw new InvalidOperationException("undo did not remove the generated PositionTrack.");
        if (editor.CanUndo)
            throw new InvalidOperationException("one drag created more than one undo entry.");
        AssertResolved(level, 0, 0);
        editor.Redo();
        if (level.ActionStore.Actions.Count(candidate => candidate.EventType == "PositionTrack") != 1)
            throw new InvalidOperationException("redo did not restore exactly one PositionTrack.");
        AssertResolved(level, 1.25, -0.75);

        if (!editor.AddPositionTrackFromDrag(2, 0.5, 0.25))
            throw new InvalidOperationException("second Alt-drag delta was rejected.");
        LevelAction[] afterSecondDrag = level.ActionStore.Actions
            .Where(candidate => candidate.Floor == 2 && candidate.EventType == "PositionTrack")
            .ToArray();
        if (afterSecondDrag.Length != 1)
            throw new InvalidOperationException(
                $"repeated Alt-drag created {afterSecondDrag.Length} PositionTrack actions instead of consolidating to one.");
        JsonArray accumulatedOffset =
            (afterSecondDrag[0].PropertyOverrides ?? afterSecondDrag[0].SourceProperties)?["positionOffset"] as JsonArray
            ?? throw new InvalidOperationException("consolidated PositionTrack positionOffset is missing.");
        AssertNear(1.75, accumulatedOffset[0]?.GetValue<double>() ?? double.NaN, "accumulated offset X");
        AssertNear(-0.5, accumulatedOffset[1]?.GetValue<double>() ?? double.NaN, "accumulated offset Y");
        AssertResolved(level, 1.75, -0.5);

        editor.Undo();
        LevelAction[] afterSecondUndo = level.ActionStore.Actions
            .Where(candidate => candidate.Floor == 2 && candidate.EventType == "PositionTrack")
            .ToArray();
        if (afterSecondUndo.Length != 1)
            throw new InvalidOperationException("undoing the second Alt-drag must preserve the first PositionTrack.");
        JsonArray restoredOffset =
            (afterSecondUndo[0].PropertyOverrides ?? afterSecondUndo[0].SourceProperties)?["positionOffset"] as JsonArray
            ?? throw new InvalidOperationException("restored PositionTrack positionOffset is missing.");
        AssertNear(1.25, restoredOffset[0]?.GetValue<double>() ?? double.NaN, "restored offset X");
        AssertNear(-0.75, restoredOffset[1]?.GetValue<double>() ?? double.NaN, "restored offset Y");
        editor.Redo();
        AssertResolved(level, 1.75, -0.5);

        LevelDocument rangeLevel = LevelDocument.CreateSynthetic(7);
        var rangeEditor = new EditorSession(rangeLevel);
        object? rangeResult = createRange.Invoke(rangeEditor, [2, 4, 1.0, -0.5]);
        if (rangeResult is bool rangeCreated && !rangeCreated)
            throw new InvalidOperationException("selected-range Alt-drag delta was rejected.");

        StaticTrackTransform[] rangeTransforms = TrackTransformResolver.ResolveStatic(rangeLevel);
        for (int floor = 0; floor < rangeLevel.FloorCount; floor++)
        {
            double expectedX = rangeLevel.Positions[floor].X +
                (floor is >= 2 and <= 4 ? PathBuilder.DefaultLongTileSize : 0.0);
            double expectedY = rangeLevel.Positions[floor].Y +
                (floor is >= 2 and <= 4 ? -0.5 * PathBuilder.DefaultLongTileSize : 0.0);
            AssertNear(expectedX, rangeTransforms[floor].X, $"selected-range X floor {floor}");
            AssertNear(expectedY, rangeTransforms[floor].Y, $"selected-range Y floor {floor}");
        }

        if (rangeEditor.GetActionsAtFloor(2).Count(action => action.EventType == "PositionTrack") != 1)
            throw new InvalidOperationException("selected-range Alt-drag must create/start one PositionTrack at the first floor.");
        if (rangeEditor.GetActionsAtFloor(5).Count(action => action.EventType == "PositionTrack") != 1)
            throw new InvalidOperationException("selected-range Alt-drag must create a compensating PositionTrack after the last floor.");

        string saved = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-PositionTrackDrag-{Guid.NewGuid():N}.adofai");
        try
        {
            editor.SaveAsync(saved).GetAwaiter().GetResult();
            JsonObject root = LooseAdoFaiJson.ParseObject(saved);
            JsonObject savedAction = root["actions"]!.AsArray().Select(node => node!.AsObject())
                .Single(item => item["eventType"]?.GetValue<string>() == "PositionTrack");
            if (savedAction["floor"]?.GetValue<int>() != 2 ||
                savedAction["positionOffset"] is not JsonArray savedOffset)
            {
                throw new InvalidOperationException("saved PositionTrack lost floor or offset.");
            }
            AssertNear(1.75, savedOffset[0]?.GetValue<double>() ?? double.NaN, "saved offset X");
            AssertNear(-0.5, savedOffset[1]?.GetValue<double>() ?? double.NaN, "saved offset Y");
            LevelDocument reloaded = WpfLevelLoader.Load(saved).Document;
            AssertResolved(reloaded, 1.75, -0.5);

            var reloadedEditor = new EditorSession(reloaded);
            LevelAction reloadedPositionTrack = reloadedEditor.GetActionsAtFloor(2)
                .Single(candidate => candidate.EventType == "PositionTrack");
            reloadedEditor.DeleteAction(reloadedPositionTrack);
            if (reloaded.ActionStore.Actions.Any(candidate => candidate.EventType == "PositionTrack"))
                throw new InvalidOperationException("source-backed PositionTrack deletion left an action in the document.");
            AssertResolved(reloaded, 0, 0);
        }
        finally
        {
            File.Delete(saved);
        }

        string viewportSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "src", "ExtremeEditor.NativeRenderer", "src", "renderer.cpp"));
        if (!viewportSource.Contains("EE_INPUT_MODIFIER_ALT", StringComparison.Ordinal) ||
            !viewportSource.Contains("WM_LBUTTONUP", StringComparison.Ordinal) ||
            !viewportSource.Contains("WM_CAPTURECHANGED", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("native viewport lacks Alt-drag/cancel gesture routing.");
        }

        string dragSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "src", "ExtremeEditor.NativeRenderer", "src", "position_track_drag.h"));
        string backendHeader = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "src", "ExtremeEditor.NativeRenderer", "src", "d2d_backend.h"));
        if (!dragSource.Contains("first_floor", StringComparison.Ordinal) ||
            !dragSource.Contains("last_floor", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "native Alt-drag does not freeze the selected first/last floor range.");
        }
        if (!viewportSource.Contains("position_drag_preview", StringComparison.Ordinal) ||
            !backendHeader.Contains("PositionTrackDragPreview", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "native renderer lacks transient selected-range PositionTrack drag preview.");
        }
    }

    private static void AssertResolved(LevelDocument level, double dx, double dy)
    {
        StaticTrackTransform[] transforms = TrackTransformResolver.ResolveStatic(level);
        NativeFloor[] snapshot = FlatNativeLevelSnapshotBuilder.BuildProfiled(level).Snapshot.Floors;
        AssertNear(level.Positions[1].X, transforms[1].X, "prior floor untouched");
        for (int floor = 2; floor < level.FloorCount; floor++)
        {
            AssertNear(level.Positions[floor].X + dx * PathBuilder.DefaultLongTileSize,
                transforms[floor].X, "renderer resolved X");
            AssertNear(level.Positions[floor].Y + dy * PathBuilder.DefaultLongTileSize,
                transforms[floor].Y, "renderer resolved Y");
            AssertNear(transforms[floor].X, snapshot[floor].X, "native snapshot X");
            AssertNear(transforms[floor].Y, snapshot[floor].Y, "native snapshot Y");
        }
    }

    private static void AssertNear(double expected, double actual, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 0.000001)
            throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}.");
    }
}
