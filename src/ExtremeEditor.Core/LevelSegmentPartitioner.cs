using System.Text.Json.Nodes;

namespace ExtremeEditor.Core;

/// <summary>
/// A floor-aligned, independently editable slice of a chart.
/// The segment owns the angle that enters its first floor (except floor zero),
/// so concatenating DestinationAngles reconstructs the original angleData
/// without inventing a bridge tile.
/// </summary>
public sealed record LevelSegment(
    int StartFloor,
    int EndFloorExclusive,
    double[] DestinationAngles,
    LevelAction[] Actions,
    LevelDecoration[] Decorations)
{
    public int FloorCount => EndFloorExclusive - StartFloor;
}

/// <summary>
/// Floor-aligned, non-destructive partitioning and reassembly of a LevelDocument.
/// This does not provide arbitrary-time cuts, segment-local timing state,
/// semantic event conflict resolution, or source-file serialization.
/// </summary>
public static class LevelSegmentPartitioner
{
    public static IReadOnlyList<LevelSegment> Split(
        LevelDocument source, params int[] cutFloors)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cutFloors);

        int floorCount = source.FloorCount;
        if (floorCount == 0 || source.Angles.Length != floorCount - 1)
            throw new ArgumentException("Source floor and angle counts do not agree.", nameof(source));

        int previous = 0;
        foreach (int cut in cutFloors)
        {
            if (cut <= previous || cut >= floorCount)
                throw new ArgumentException(
                    "Cuts must be strictly increasing, unique and inside the chart's floor range.",
                    nameof(cutFloors));
            previous = cut;
        }

        // Reject unsupported source data rather than silently losing it.
        foreach (LevelAction action in source.ActionStore.Actions)
        {
            if (action.Floor < 0 || action.Floor >= floorCount)
                throw new ArgumentException("Source contains an action outside its floor range.", nameof(source));
        }
        foreach (LevelDecoration decoration in source.Decorations)
        {
            if (decoration.Floor is int floor && (floor < 0 || floor >= floorCount))
                throw new ArgumentException("Source contains a decoration outside its floor range.", nameof(source));
        }

        var result = new List<LevelSegment>(cutFloors.Length + 1);
        int start = 0;
        for (int i = 0; i <= cutFloors.Length; i++)
        {
            int end = i < cutFloors.Length ? cutFloors[i] : floorCount;

            // angleData[j] enters floor j+1. The incoming angle at a
            // partition boundary is owned by the right-hand segment.
            int firstAngle = Math.Max(0, start - 1);
            int angleCount = end - Math.Max(1, start);
            double[] angles = source.Angles.AsSpan(firstAngle, angleCount).ToArray();

            LevelAction[] actions = source.ActionStore.Actions
                .Where(action => action.Floor >= start && action.Floor < end)
                .Select(action => CloneAction(action, action.Floor - start))
                .ToArray();

            // Floor-less decorations are project-wide state; the template
            // retains them and Split never duplicates them into a segment.
            LevelDecoration[] decorations = source.Decorations
                .Where(decoration => decoration.Floor is int floor && floor >= start && floor < end)
                .Select(decoration => CloneDecoration(decoration, decoration.Floor!.Value - start))
                .ToArray();

            result.Add(new LevelSegment(start, end, angles, actions, decorations));
            start = end;
        }

        return result;
    }

    public static LevelDocument Merge(
        LevelDocument template, IReadOnlyList<LevelSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
            throw new ArgumentException("At least one segment is required.", nameof(segments));

        var angles = new List<double>();
        var actions = new List<LevelAction>();
        var decorations = new List<LevelDecoration>();
        int expectedStart = 0;

        foreach (LevelSegment segment in segments)
        {
            if (segment is null ||
                segment.StartFloor != expectedStart ||
                segment.EndFloorExclusive <= segment.StartFloor ||
                segment.DestinationAngles is null ||
                segment.Actions is null ||
                segment.Decorations is null)
            {
                throw new ArgumentException(
                    "Segments must be nonempty, ordered and cover the chart without gaps or overlaps.",
                    nameof(segments));
            }

            int expectedAngleCount = segment.StartFloor == 0
                ? segment.FloorCount - 1
                : segment.FloorCount;
            if (segment.DestinationAngles.Length != expectedAngleCount)
                throw new ArgumentException("Segment destination angle count is invalid.", nameof(segments));

            angles.AddRange(segment.DestinationAngles);
            foreach (LevelAction action in segment.Actions)
            {
                if (action is null || action.Floor < 0 || action.Floor >= segment.FloorCount)
                    throw new ArgumentException("A segment action has an invalid local floor.", nameof(segments));
                actions.Add(CloneAction(action, checked(segment.StartFloor + action.Floor)));
            }

            foreach (LevelDecoration decoration in segment.Decorations)
            {
                if (decoration is null ||
                    decoration.Floor is not int localFloor ||
                    localFloor < 0 || localFloor >= segment.FloorCount)
                {
                    throw new ArgumentException(
                        "Segment decorations must have valid local floors; project-wide decorations belong to the template.",
                        nameof(segments));
                }
                decorations.Add(CloneDecoration(decoration, checked(segment.StartFloor + localFloor)));
            }

            expectedStart = segment.EndFloorExclusive;
        }

        // Preserve global decorations exactly once. SourceIndex gives their
        // original position relative to floor-attached decorations.
        decorations.AddRange(template.Decorations
            .Where(decoration => decoration.Floor is null)
            .Select(decoration => CloneDecoration(decoration, null)));

        LevelDecoration[] orderedDecorations = decorations
            .OrderBy(decoration => decoration.SourceIndex < 0 ? int.MaxValue : decoration.SourceIndex)
            .ToArray();

        double[] mergedAngles = angles.ToArray();
        if (mergedAngles.Length != expectedStart - 1)
            throw new ArgumentException("Merged angles do not cover all destination floors.", nameof(segments));

        // Preserve resolved source geometry (including ScaleRadius) when the
        // source angles are unchanged. For edited angles, the plain geometric
        // fallback must eventually be replaced by a full event-aware resolver.
        bool sameAngles = template.Angles.AsSpan().SequenceEqual(mergedAngles) &&
                          template.Positions.Length == expectedStart;
        var positions = sameAngles
            ? template.Positions.ToArray()
            : PathBuilder.BuildPositions(mergedAngles);

        var merged = new LevelDocument
        {
            SourcePath = template.SourcePath,
            Angles = mergedAngles,
            Positions = positions,
            Bounds = PathBuilder.CalculateBounds(positions),
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal),
            ActionsByFloor = LevelActionStore.Empty.DictionaryView,
            InitialBpm = template.InitialBpm,
            SongFilename = template.SongFilename,
            SongVolumePercent = template.SongVolumePercent,
            OffsetMilliseconds = template.OffsetMilliseconds,
            PitchPercent = template.PitchPercent,
            CountdownTicks = template.CountdownTicks,
            SeparateCountdownTime = template.SeparateCountdownTime,
            DefaultHitSound = template.DefaultHitSound,
            HitSoundVolumePercent = template.HitSoundVolumePercent,
            CameraSettings = template.CameraSettings
        };

        merged.ReplaceActions(actions);
        merged.ReplaceDecorations(orderedDecorations);
        return merged;
    }

    private static LevelAction CloneAction(LevelAction action, int localOrGlobalFloor)
    {
        JsonObject? sourceProperties = action.SourceProperties is null
            ? null
            : (JsonObject)action.SourceProperties.DeepClone();
        JsonObject? overrides = action.PropertyOverrides is null
            ? null
            : (JsonObject)action.PropertyOverrides.DeepClone();

        // Keep the raw per-action properties consistent with the model's
        // locally rebased floor; never mutate the input JSON objects.
        RebaseTopLevelFloor(sourceProperties, localOrGlobalFloor);
        RebaseTopLevelFloor(overrides, localOrGlobalFloor);
        return action with
        {
            Floor = localOrGlobalFloor,
            SourceProperties = sourceProperties,
            PropertyOverrides = overrides
        };
    }

    private static void RebaseTopLevelFloor(JsonObject? properties, int floor)
    {
        if (properties is not null && properties.ContainsKey("floor"))
            properties["floor"] = floor;
    }

    private static LevelDecoration CloneDecoration(LevelDecoration decoration, int? floor) =>
        decoration with
        {
            Floor = floor,
            Properties = (JsonObject)decoration.Properties.DeepClone()
        };
}
