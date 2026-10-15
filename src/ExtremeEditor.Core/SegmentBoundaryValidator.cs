using System.Numerics;
using System.Text.Json;

namespace ExtremeEditor.Core;

/// <summary>
/// Why a floor-aligned Segment roundtrip cannot be certified as preserving
/// the reference chart's modeled timing, content and resolved geometry.
/// This is not a stock-game playability or lossless .adofai export proof.
/// </summary>
public enum SegmentBoundaryIssueKind : byte
{
    EventDrift,
    TimingDrift,
    GeometryDrift,
    PlaybackPoseDrift,
    SampleAnchorMismatch,
    UnsupportedCrossBoundaryEffect
}

public sealed record SegmentBoundaryIssue(
    SegmentBoundaryIssueKind Kind,
    int Floor,
    string Detail);

public sealed record SegmentBoundarySampleAnchor(
    int BoundaryFloor,
    int SampleRate,
    long EntrySample);

public sealed record SegmentBoundaryValidationResult(
    IReadOnlyList<SegmentBoundaryIssue> Issues)
{
    public bool CanExportModeledRoundTrip => Issues.Count == 0;
}

/// <summary>
/// Compares the unchanged floor-aligned roundtrip against the original chart.
/// This API does not approve intentional edits, arbitrary-time cuts, or stock
/// .adofai export. Unknown potentially stateful boundary effects fail closed.
/// </summary>
public static class SegmentBoundaryValidator
{
    // Floor time is calculated in double; no floor-boundary timestamps may
    // drift by more than a fraction of an audio sample at common rates.
    private const double TimingToleranceSeconds = 1e-7;
    private const double AngleToleranceRadians = 1e-7;
    private const float GeometryTolerance = 1e-4f;
    private const double ProgressTolerance = 1e-7;

    public static SegmentBoundaryValidationResult ValidateRoundTrip(
        LevelDocument reference,
        IReadOnlyList<LevelSegment> segments,
        IReadOnlyList<SegmentBoundarySampleAnchor>? sampleAnchors = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(segments);

        // Merge enforces valid local event floors, contiguous floor coverage,
        // right-hand ownership of incoming angles and non-null payloads.
        LevelDocument roundtrip = LevelSegmentPartitioner.Merge(reference, segments);
        if (roundtrip.FloorCount != reference.FloorCount ||
            reference.Angles.Length != reference.FloorCount - 1)
        {
            throw new ArgumentException(
                "Reference and Segment floor ranges must have the same valid coverage.",
                nameof(segments));
        }

        var boundaryFloors = segments.Skip(1)
            .Select(static segment => segment.StartFloor).ToArray();
        ValidateAnchors(sampleAnchors, boundaryFloors);

        var issues = new List<SegmentBoundaryIssue>();
        CompareEvents(reference, roundtrip, issues);
        CompareGeometry(reference, roundtrip, issues);

        // Compare not just each Segment's local starting BPM/direction, but
        // the entire state accumulated from floor 0 through both sides of
        // every cut. No fresh standalone TimingMap is built per Segment.
        TimingMap referenceTiming = TimingMapBuilder.Build(reference);
        TimingMap reconstructedTiming = TimingMapBuilder.Build(roundtrip);
        CompareTiming(referenceTiming, reconstructedTiming, issues);

        ComparePlayback(reference, roundtrip,
            referenceTiming, reconstructedTiming, issues);
        CompareAnchors(reference, roundtrip,
            referenceTiming, reconstructedTiming, sampleAnchors, issues);

        foreach (int boundary in boundaryFloors)
            CheckUnsupportedEffects(reference, roundtrip, boundary, issues);

        return new SegmentBoundaryValidationResult(issues.ToArray());
    }

    private static void CompareEvents(
        LevelDocument reference, LevelDocument roundtrip,
        List<SegmentBoundaryIssue> issues)
    {
        LevelAction[] original = reference.ActionStore.Actions.ToArray();
        LevelAction[] rebuilt = roundtrip.ActionStore.Actions.ToArray();

        if (original.Length != rebuilt.Length)
        {
            issues.Add(new(
                SegmentBoundaryIssueKind.EventDrift, -1,
                $"Action count changed from {original.Length} to {rebuilt.Length}."));
        }

        for (int i = 0; i < Math.Min(original.Length, rebuilt.Length); i++)
        {
            // Serialize the full typed event, including SourceIndex, source
            // properties and overrides. This is deliberately conservative:
            // unknown nested JSON may not be silently discarded.
            if (!JsonEqual(original[i], rebuilt[i]))
            {
                issues.Add(new(
                    SegmentBoundaryIssueKind.EventDrift,
                    original[i].Floor,
                    $"Action at ordered index {i} differs after reassembly."));
            }
        }

        LevelDecoration[] originalDecorations = reference.Decorations.ToArray();
        LevelDecoration[] rebuiltDecorations = roundtrip.Decorations.ToArray();
        if (originalDecorations.Length != rebuiltDecorations.Length)
        {
            issues.Add(new(
                SegmentBoundaryIssueKind.EventDrift, -1,
                $"Decoration count changed from {originalDecorations.Length} to {rebuiltDecorations.Length}."));
        }
        for (int i = 0;
             i < Math.Min(originalDecorations.Length, rebuiltDecorations.Length); i++)
        {
            if (!JsonEqual(originalDecorations[i], rebuiltDecorations[i]))
            {
                issues.Add(new(
                    SegmentBoundaryIssueKind.EventDrift,
                    originalDecorations[i].Floor ?? -1,
                    $"Decoration at ordered index {i} differs after reassembly."));
            }
        }

        // Global chart settings affect modeled timing and audio alignment.
        // The current Merge copies the settings from the trusted reference;
        // still explicitly reject future regressions if that behavior changes.
        if (!Near(reference.InitialBpm, roundtrip.InitialBpm, 0) ||
            !Near(reference.OffsetMilliseconds, roundtrip.OffsetMilliseconds, 0) ||
            !Near(reference.PitchPercent, roundtrip.PitchPercent, 0) ||
            reference.CountdownTicks != roundtrip.CountdownTicks ||
            reference.SeparateCountdownTime != roundtrip.SeparateCountdownTime ||
            !string.Equals(reference.SongFilename, roundtrip.SongFilename,
                StringComparison.Ordinal))
        {
            issues.Add(new(
                SegmentBoundaryIssueKind.EventDrift, -1,
                "The roundtrip changed modeled project-wide audio/timing settings."));
        }
    }

    private static void CompareGeometry(
        LevelDocument reference, LevelDocument roundtrip,
        List<SegmentBoundaryIssue> issues)
    {
        if (reference.Angles.Length != roundtrip.Angles.Length)
        {
            issues.Add(new(
                SegmentBoundaryIssueKind.GeometryDrift, -1,
                "Destination angle count changed."));
        }
        for (int i = 0; i < Math.Min(reference.Angles.Length, roundtrip.Angles.Length); i++)
        {
            // Comparing raw angles also preserves sentinel 999 and prevents
            // accepting a geometry coincidence that changed note semantics.
            if (!double.IsFinite(reference.Angles[i]) ||
                !double.IsFinite(roundtrip.Angles[i]) ||
                reference.Angles[i] != roundtrip.Angles[i])
            {
                issues.Add(new(
                    SegmentBoundaryIssueKind.GeometryDrift, i + 1,
                    $"Incoming destination angle of floor {i + 1} changed."));
            }
        }

        if (reference.Positions.Length != roundtrip.Positions.Length)
        {
            issues.Add(new(
                SegmentBoundaryIssueKind.GeometryDrift, -1,
                "Resolved world position count changed."));
        }
        for (int i = 0;
             i < Math.Min(reference.Positions.Length, roundtrip.Positions.Length); i++)
        {
            if (!NearVector(reference.Positions[i], roundtrip.Positions[i]))
                issues.Add(new(
                    SegmentBoundaryIssueKind.GeometryDrift, i,
                    $"Resolved world-space floor position {i} changed."));
        }
    }

    private static void CompareTiming(
        TimingMap reference, TimingMap roundtrip,
        List<SegmentBoundaryIssue> issues)
    {
        if (reference.Floors.Count != roundtrip.Floors.Count)
        {
            issues.Add(new(
                SegmentBoundaryIssueKind.TimingDrift, -1,
                "Timing-map floor count changed."));
        }

        for (int i = 0; i < Math.Min(reference.Floors.Count, roundtrip.Floors.Count); i++)
        {
            FloorTiming a = reference.Floors[i];
            FloorTiming b = roundtrip.Floors[i];
            if (a.Floor != b.Floor ||
                a.IsCcw != b.IsCcw ||
                a.MidSpin != b.MidSpin ||
                !Near(a.EntryTime, b.EntryTime, TimingToleranceSeconds) ||
                !Near(a.ExitTime, b.ExitTime, TimingToleranceSeconds) ||
                !Near(a.PauseSeconds, b.PauseSeconds, TimingToleranceSeconds) ||
                !Near(a.EntryAngle, b.EntryAngle, AngleToleranceRadians) ||
                !Near(a.ExitAngle, b.ExitAngle, AngleToleranceRadians) ||
                !Near(a.AngleMoved, b.AngleMoved, AngleToleranceRadians) ||
                !Near(a.Bpm, b.Bpm, 1e-7))
            {
                issues.Add(new(
                    SegmentBoundaryIssueKind.TimingDrift, i,
                    $"Modeled timing/state differs at floor {i} " +
                    "(entry/exit, effective BPM, direction, pause, or rotation)."));
            }
        }
    }

    private static void ComparePlayback(
        LevelDocument reference, LevelDocument roundtrip,
        TimingMap originalTiming, TimingMap rebuiltTiming,
        List<SegmentBoundaryIssue> issues)
    {
        // Evaluate both poses at the SAME global chart times. Testing
        // individual Segments at local time=0 would miss cumulative drift.
        // Entry, late-pause, in-rotation, and exit-adjacent samples detect
        // discontinuities before and after each boundary.
        for (int i = 0; i < originalTiming.Floors.Count; i++)
        {
            FloorTiming floor = originalTiming.Floors[i];
            double duration = floor.ExitTime - floor.EntryTime;
            if (!double.IsFinite(duration) || duration < 0)
            {
                issues.Add(new(
                    SegmentBoundaryIssueKind.PlaybackPoseDrift, i,
                    "Reference chart has a nonfinite or reversed floor interval."));
                continue;
            }

            double[] fractions = [0, 0.25, 0.5, 0.75, 1];
            foreach (double fraction in fractions)
            {
                double time = floor.EntryTime + duration * fraction;
                PlaybackPose a = originalTiming.GetPose(reference, time);
                PlaybackPose b = rebuiltTiming.GetPose(roundtrip, time);
                if (!NearPose(a, b))
                {
                    issues.Add(new(
                        SegmentBoundaryIssueKind.PlaybackPoseDrift, i,
                        $"Planet pose/progress differs at reference floor {i}, fraction {fraction}."));
                    break;
                }
            }
        }
    }

    private static void ValidateAnchors(
        IReadOnlyList<SegmentBoundarySampleAnchor>? anchors, int[] boundaries)
    {
        if (anchors is null)
            return;

        var expected = boundaries.ToHashSet();
        var seen = new HashSet<int>();
        foreach (SegmentBoundarySampleAnchor? anchor in anchors)
        {
            if (anchor is null ||
                anchor.SampleRate <= 0 ||
                !expected.Contains(anchor.BoundaryFloor) ||
                !seen.Add(anchor.BoundaryFloor))
                throw new ArgumentException(
                    "Each sample anchor must identify a distinct Segment floor boundary and a positive rate.",
                    nameof(anchors));
        }

        if (seen.Count != expected.Count)
            throw new ArgumentException(
                "When specified, floor-entry sample anchors must cover every Segment boundary.",
                nameof(anchors));
    }

    private static void CompareAnchors(
        LevelDocument reference, LevelDocument roundtrip,
        TimingMap originalTiming, TimingMap reconstructedTiming,
        IReadOnlyList<SegmentBoundarySampleAnchor>? anchors,
        List<SegmentBoundaryIssue> issues)
    {
        if (anchors is null)
            return;

        foreach (SegmentBoundarySampleAnchor anchor in anchors)
        {
            int floor = anchor.BoundaryFloor;
            double expectedSeconds = PlaybackClock.ChartToAudioTime(
                reference, originalTiming.GetEntryTime(floor));
            double reconstructedSeconds = PlaybackClock.ChartToAudioTime(
                roundtrip, reconstructedTiming.GetEntryTime(floor));
            if (!double.IsFinite(expectedSeconds) ||
                !double.IsFinite(reconstructedSeconds))
            {
                issues.Add(new(
                    SegmentBoundaryIssueKind.SampleAnchorMismatch, floor,
                    "An audio-time floor-entry coordinate was not finite."));
                continue;
            }

            // Compare in long-sample coordinates; do not derive boundary
            // allocation from BPM/beats. At most one decoded sample of rounding
            // difference is allowed against BOTH reference and roundtrip.
            double expectedSample = expectedSeconds * anchor.SampleRate;
            double reconstructedSample = reconstructedSeconds * anchor.SampleRate;
            if (!double.IsFinite(expectedSample) || !double.IsFinite(reconstructedSample) ||
                Math.Abs(expectedSample - anchor.EntrySample) > 1.5 ||
                Math.Abs(reconstructedSample - anchor.EntrySample) > 1.5)
            {
                issues.Add(new(
                    SegmentBoundaryIssueKind.SampleAnchorMismatch, floor,
                    $"Floor {floor} entry is not aligned with the supplied audio sample " +
                    $"({anchor.EntrySample} at {anchor.SampleRate} Hz)."));
            }
        }
    }

    private static void CheckUnsupportedEffects(
        LevelDocument reference, LevelDocument roundtrip,
        int boundary, List<SegmentBoundaryIssue> issues)
    {
        // The current model does not carry enough state to safely reconstruct
        // animated radius/position tracks across a Segment boundary. Anything
        // active before the boundary may remain effective at that floor.
        // MoveDecorations/other unsupported positive-duration events can also
        // remain active; refuse certification rather than inventing an end.
        foreach (LevelDocument level in new[] { reference, roundtrip })
        {
            foreach (LevelAction action in level.ActionStore.Actions)
            {
                if (!action.Active || action.Floor >= boundary)
                    continue;

                bool unresolvedGeometry = action.EventType is
                    "ScaleRadius" or "PositionTrack";
                bool unresolvedTimedEffect =
                    action.Duration is > 0 &&
                    action.Kind == LevelActionKind.Unknown;
                if (!unresolvedGeometry && !unresolvedTimedEffect)
                    continue;

                // Avoid emitting duplicate descriptions for the same effect
                // when it exists unchanged in both reference and roundtrip.
                if (issues.Any(item =>
                        item.Kind == SegmentBoundaryIssueKind.UnsupportedCrossBoundaryEffect &&
                        item.Floor == boundary &&
                        item.Detail.Contains($"floor {action.Floor}: {action.EventType}",
                            StringComparison.Ordinal)))
                    continue;

                issues.Add(new(
                    SegmentBoundaryIssueKind.UnsupportedCrossBoundaryEffect,
                    boundary,
                    $"Unresolved cross-boundary effect from floor {action.Floor}: {action.EventType}."));
            }
        }
    }

    private static bool JsonEqual<T>(T left, T right) =>
        string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right),
            StringComparison.Ordinal);

    private static bool Near(double a, double b, double tolerance) =>
        double.IsFinite(a) && double.IsFinite(b) &&
        Math.Abs(a - b) <= tolerance;

    private static bool NearVector(Vector2 a, Vector2 b) =>
        float.IsFinite(a.X) && float.IsFinite(a.Y) &&
        float.IsFinite(b.X) && float.IsFinite(b.Y) &&
        Vector2.DistanceSquared(a, b) <= GeometryTolerance * GeometryTolerance;

    private static bool NearPose(PlaybackPose a, PlaybackPose b) =>
        a.Floor == b.Floor &&
        a.StationaryIsRed == b.StationaryIsRed &&
        a.IsPreStart == b.IsPreStart &&
        Near(a.Progress, b.Progress, ProgressTolerance) &&
        NearVector(a.StationaryPlanet, b.StationaryPlanet) &&
        NearVector(a.OrbitingPlanet, b.OrbitingPlanet);
}
