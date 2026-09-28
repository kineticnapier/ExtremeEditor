using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class TwirlIncrementalNativeRegression
{
    public static void Run()
    {
        VerifyExistingIconResolverSemantics();
        VerifyNegativeControl();
        VerifyTwirlActiveEditRouting();
    }

    private static void VerifyExistingIconResolverSemantics()
    {
        string[] paths =
        [
            IconAssetCache.FloorPath("SwirlRed"),
            IconAssetCache.FloorPath("SwirlBlue"),
            IconAssetCache.FloorPath("SameSpeed"),
            IconAssetCache.FloorPath("Checkpoint"),
            IconAssetCache.FloorPath("Portal")
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
                File.WriteAllBytes(path, [0x54, 0x57, 0x49, 0x52, 0x4C]);
            }

            VerifyTwirlOnlyBecomesNoIcon();
            VerifyNextPriorityIconAndUnrelatedFloor();
            VerifyIncrementalMatchesFullBuild();
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

    private static void VerifyIncrementalMatchesFullBuild()
    {
        const int targetFloor = 3;
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        LevelAction prefixCheckpoint = new(1, "Checkpoint", true, null, null, null, null)
        {
            Kind = LevelActionKind.Checkpoint,
            SourceIndex = 0
        };
        LevelAction targetTwirl = CreateTwirl(targetFloor, sourceIndex: 1);
        LevelAction prefixSpeed = new(2, "SetSpeed", true, "Multiplier", null, 1.0, null)
        {
            Kind = LevelActionKind.SetSpeed,
            SourceIndex = 5,
            SpeedRatio = 1.0
        };
        LevelAction speed = new(targetFloor, "SetSpeed", true, "Multiplier", null, 1.0, null)
        {
            Kind = LevelActionKind.SetSpeed,
            SourceIndex = 2,
            SpeedRatio = 1.0
        };
        LevelAction downstreamTwirl = CreateTwirl(4, sourceIndex: 3);
        LevelAction unrelatedCheckpoint = new(5, "Checkpoint", true, null, null, null, null)
        {
            Kind = LevelActionKind.Checkpoint,
            SourceIndex = 4
        };
        level.ReplaceActions(
            [prefixCheckpoint, prefixSpeed, targetTwirl, speed, downstreamTwirl, unrelatedCheckpoint]);

        var editor = new EditorSession(level);
        using var viewport = new NativeLevelViewport();
        viewport.SetPreparedLevel(level, NativeLevelViewport.PrepareLevel(level));
        NativeLevelSnapshot initialSnapshot = viewport.SnapshotForDiagnostics
            ?? throw new InvalidOperationException("Initial native snapshot is missing.");
        NativeFloorIconState[] prefixBefore = initialSnapshot.Floors
            .Take(targetFloor)
            .Select(ToIconState)
            .ToArray();

        LevelAction requestedAfter = targetTwirl with
        {
            Active = false,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = targetFloor,
                ["eventType"] = "Twirl",
                ["active"] = false
            }
        };
        editor.ReplaceAction(targetTwirl, requestedAfter);
        LevelAction after = editor.Document.ActionStore.Actions
            .Single(action => action.SourceIndex == targetTwirl.SourceIndex);
        NativeActionEditUpdate.Apply(viewport, level, targetTwirl, after);

        NativeLevelSnapshot incremental = viewport.SnapshotForDiagnostics
            ?? throw new InvalidOperationException("Incremental native snapshot is missing.");
        if (!ReferenceEquals(initialSnapshot, incremental))
            throw new InvalidOperationException("Twirl incremental update replaced the managed snapshot instance.");

        NativeLevelSnapshot oracle = FlatNativeLevelSnapshotBuilder.BuildProfiled(level).Snapshot;
        for (int floor = 0; floor < level.FloorCount; floor++)
        {
            AssertSameIconState(
                floor,
                incremental,
                oracle,
                "incremental result differs from full-build oracle");
        }

        for (int floor = 0; floor < targetFloor; floor++)
        {
            NativeFloorIconState afterPrefix = ToIconState(incremental.Floors[floor]);
            if (!afterPrefix.Equals(prefixBefore[floor]))
                throw new InvalidOperationException($"Floor {floor} before the suffix start was modified.");
        }

        string? targetPath = GetIconPath(incremental, targetFloor);
        if (!string.Equals(
                targetPath,
                Path.GetFullPath(IconAssetCache.FloorPath("SameSpeed")),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Incremental target floor did not reveal the SetSpeed icon.");

        int terminalFloor = level.FloorCount - 1;
        string? portalPath = GetIconPath(incremental, terminalFloor);
        if (!string.Equals(
                portalPath,
                Path.GetFullPath(IconAssetCache.FloorPath("Portal")),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Incremental suffix update lost terminal Portal semantics.");
    }

    private static void VerifyTwirlOnlyBecomesNoIcon()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        LevelAction twirl = CreateTwirl(floor: 2, sourceIndex: 0);
        level.ReplaceActions([twirl]);
        NativeLevelSnapshot before = FlatNativeLevelSnapshotBuilder.BuildProfiled(level).Snapshot;

        level.ReplaceActions([twirl with { Active = false }]);
        NativeLevelSnapshot after = FlatNativeLevelSnapshotBuilder.BuildProfiled(level).Snapshot;

        string? beforePath = GetIconPath(before, twirl.Floor);
        if (beforePath is null || !IsSwirlPath(beforePath))
            throw new InvalidOperationException("Twirl-only fixture did not resolve a Twirl floor icon.");
        if (GetIconPath(after, twirl.Floor) is not null)
            throw new InvalidOperationException("Inactive Twirl retained an icon in the existing resolver.");
    }

    private static void VerifyNextPriorityIconAndUnrelatedFloor()
    {
        const int targetFloor = 3;
        const int downstreamTwirlFloor = 4;
        const int unrelatedFloor = 5;
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        LevelAction twirl = CreateTwirl(targetFloor, sourceIndex: 0);
        LevelAction speed = new(targetFloor, "SetSpeed", true, "Multiplier", null, 1.0, null)
        {
            Kind = LevelActionKind.SetSpeed,
            SourceIndex = 1,
            SpeedRatio = 1.0
        };
        LevelAction checkpoint = new(unrelatedFloor, "Checkpoint", true, null, null, null, null)
        {
            Kind = LevelActionKind.Checkpoint,
            SourceIndex = 2
        };
        LevelAction downstreamTwirl = CreateTwirl(downstreamTwirlFloor, sourceIndex: 3);

        level.ReplaceActions([twirl, speed, downstreamTwirl, checkpoint]);
        NativeLevelSnapshot before = FlatNativeLevelSnapshotBuilder.BuildProfiled(level).Snapshot;
        level.ReplaceActions([twirl with { Active = false }, speed, downstreamTwirl, checkpoint]);
        NativeLevelSnapshot after = FlatNativeLevelSnapshotBuilder.BuildProfiled(level).Snapshot;

        string? beforeTarget = GetIconPath(before, targetFloor);
        string? afterTarget = GetIconPath(after, targetFloor);
        if (beforeTarget is null || !IsSwirlPath(beforeTarget))
            throw new InvalidOperationException("Twirl did not outrank SetSpeed in the existing icon resolver.");
        if (!string.Equals(
                afterTarget,
                Path.GetFullPath(IconAssetCache.FloorPath("SameSpeed")),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Inactive Twirl did not reveal the next icon selected by the existing priority resolver.");
        }

        NativeFloor beforeDownstream = before.Floors[downstreamTwirlFloor];
        NativeFloor afterDownstream = after.Floors[downstreamTwirlFloor];
        string? beforeDownstreamPath = GetIconPath(before, downstreamTwirlFloor);
        string? afterDownstreamPath = GetIconPath(after, downstreamTwirlFloor);
        if (string.Equals(beforeDownstreamPath, afterDownstreamPath, StringComparison.OrdinalIgnoreCase) &&
            beforeDownstream.IconFlags == afterDownstream.IconFlags &&
            MathF.Abs(beforeDownstream.IconAngle - afterDownstream.IconAngle) < 0.000001f)
        {
            throw new InvalidOperationException(
                "Existing Twirl parity semantics did not affect the downstream Twirl fixture.");
        }

        string? beforeUnrelated = GetIconPath(before, unrelatedFloor);
        string? afterUnrelated = GetIconPath(after, unrelatedFloor);
        if (!string.Equals(beforeUnrelated, afterUnrelated, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Twirl edit changed an unrelated floor icon.");
    }

    private static void VerifyTwirlActiveEditRouting()
    {
        const int targetFloor = 3;
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        LevelAction before = CreateTwirl(targetFloor, sourceIndex: 12);
        level.ReplaceActions([before]);
        var editor = new EditorSession(level);
        using var viewport = new NativeLevelViewport();
        viewport.SetPreparedLevel(level, NativeLevelViewport.PrepareLevel(level));

        LevelAction requestedAfter = before with
        {
            Active = false,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = targetFloor,
                ["eventType"] = "Twirl",
                ["active"] = false
            }
        };
        editor.ReplaceAction(before, requestedAfter);
        LevelAction after = editor.Document.ActionStore.Actions
            .Single(action => action.SourceIndex == before.SourceIndex);

        NativeActionEditPlan plan = NativeActionEditUpdate.CreatePlan(before, after);
        if (plan.Kind != NativeActionEditKind.FloorIconsFrom || plan.StartFloor != targetFloor)
        {
            throw new InvalidOperationException(
                $"Twirl active edit plan is invalid: kind={plan.Kind}, startFloor={plan.StartFloor}.");
        }

        NativeLevelUpdateDiagnosticCounts counts = RunApply(viewport, level, before, after);
        if (counts.FullLevelReplacementRequests != 0 ||
            counts.PrepareLevelCalls != 0 ||
            counts.FlatSnapshotBuilds != 0 ||
            counts.FloorIconUpdateRequests != 1 ||
            counts.LastFloorIconUpdateStartFloor != targetFloor ||
            counts.ActionEditRoutingInvocations != 1)
        {
            throw new InvalidOperationException(
                "Twirl edit triggered full native snapshot replacement. " +
                $"replacements={counts.FullLevelReplacementRequests}, " +
                $"prepareLevel={counts.PrepareLevelCalls}, " +
                $"flatSnapshotBuilds={counts.FlatSnapshotBuilds}, " +
                $"iconUpdates={counts.FloorIconUpdateRequests}, " +
                $"startFloor={counts.LastFloorIconUpdateStartFloor}, " +
                $"routingInvocations={counts.ActionEditRoutingInvocations}.");
        }
    }

    private static void VerifyNegativeControl()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        LevelAction before = CreateTwirl(floor: 3, sourceIndex: 4);
        LevelAction after = before with { Floor = 4, Active = false };
        level.ReplaceActions([before]);
        using var viewport = new NativeLevelViewport();
        viewport.SetPreparedLevel(level, NativeLevelViewport.PrepareLevel(level));

        NativeActionEditPlan plan = NativeActionEditUpdate.CreatePlan(before, after);
        if (plan.Kind != NativeActionEditKind.FullLevel)
            throw new InvalidOperationException("Twirl floor change must retain the full-level plan.");

        NativeLevelUpdateDiagnosticCounts counts = RunApply(viewport, level, before, after);
        if (counts.FullLevelReplacementRequests != 1 ||
            counts.FloorIconUpdateRequests != 0 ||
            counts.ActionEditRoutingInvocations != 1)
        {
            throw new InvalidOperationException(
                "Twirl floor-change negative control did not retain full replacement. " +
                $"replacements={counts.FullLevelReplacementRequests}, " +
                $"iconUpdates={counts.FloorIconUpdateRequests}, " +
                $"routingInvocations={counts.ActionEditRoutingInvocations}.");
        }
    }

    private static NativeLevelUpdateDiagnosticCounts RunApply(
        NativeLevelViewport viewport,
        LevelDocument level,
        LevelAction before,
        LevelAction after)
    {
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

    private static LevelAction CreateTwirl(int floor, int sourceIndex) =>
        new(floor, "Twirl", true, null, null, null, null)
        {
            Kind = LevelActionKind.Twirl,
            SourceIndex = sourceIndex,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "Twirl",
                ["active"] = true
            }
        };

    private static string? GetIconPath(NativeLevelSnapshot snapshot, int floor)
    {
        uint iconId = snapshot.Floors[floor].IconId;
        return iconId == NativeFloor.NoIcon || iconId >= snapshot.IconAssets.Length
            ? null
            : snapshot.IconAssets[iconId].ImagePath;
    }

    private static NativeFloorIconState ToIconState(NativeFloor floor) => new()
    {
        IconId = floor.IconId,
        IconFlags = floor.IconFlags,
        IconAngle = floor.IconAngle
    };

    private static void AssertSameIconState(
        int floor,
        NativeLevelSnapshot actual,
        NativeLevelSnapshot expected,
        string message)
    {
        NativeFloor actualFloor = actual.Floors[floor];
        NativeFloor expectedFloor = expected.Floors[floor];
        string? actualPath = GetIconPath(actual, floor);
        string? expectedPath = GetIconPath(expected, floor);
        if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
            actualFloor.IconFlags != expectedFloor.IconFlags ||
            MathF.Abs(actualFloor.IconAngle - expectedFloor.IconAngle) > 0.000001f)
        {
            throw new InvalidOperationException(
                $"Floor {floor} {message}: " +
                $"actual=({actualPath}, {actualFloor.IconFlags}, {actualFloor.IconAngle}), " +
                $"expected=({expectedPath}, {expectedFloor.IconFlags}, {expectedFloor.IconAngle}).");
        }
    }

    private static bool IsSwirlPath(string path) =>
        string.Equals(path, Path.GetFullPath(IconAssetCache.FloorPath("SwirlRed")), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, Path.GetFullPath(IconAssetCache.FloorPath("SwirlBlue")), StringComparison.OrdinalIgnoreCase);
}
