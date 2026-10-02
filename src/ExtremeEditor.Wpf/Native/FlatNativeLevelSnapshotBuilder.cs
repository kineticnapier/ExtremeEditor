using System.Diagnostics;
using System.IO;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf.Native;

internal static class FlatNativeLevelSnapshotBuilder
{
    private const float TwoPi = MathF.PI * 2f;
    private const float DegreesToRadians = MathF.PI / 180f;

    internal static NativeLevelSnapshotBuildResult BuildProfiled(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        NativeLevelUpdateDiagnostics.RecordFlatSnapshotBuild();

        var totalWatch = Stopwatch.StartNew();

        LevelActionStore empty = LevelActionStore.Empty;
        var geometryOnlyLevel = new LevelDocument
        {
            SourcePath = level.SourcePath,
            Angles = level.Angles,
            Positions = level.Positions,
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = empty.DictionaryView,
            ActionStore = empty,
            InitialBpm = level.InitialBpm,
            SongFilename = level.SongFilename,
            OffsetMilliseconds = level.OffsetMilliseconds,
            PitchPercent = level.PitchPercent,
            CountdownTicks = level.CountdownTicks,
            SeparateCountdownTime = level.SeparateCountdownTime,
            DefaultHitSound = level.DefaultHitSound,
            HitSoundVolumePercent = level.HitSoundVolumePercent,
            Bounds = level.Bounds
        };

        var phaseWatch = Stopwatch.StartNew();
        NativeLevelSnapshotBuildResult geometryResult =
            NativeLevelSnapshotBuilder.BuildGeometryProfiled(geometryOnlyLevel);
        phaseWatch.Stop();
        LogPhase("baseGeometry", phaseWatch.Elapsed);
        NativeLevelSnapshot geometrySnapshot = geometryResult.Snapshot;
        NativeFloor[] floors = geometrySnapshot.Floors;
        double[] angles = level.Angles;

        phaseWatch.Restart();
        NativeTrackVisual[] trackVisuals = TrackVisualResolver.Resolve(level);
        phaseWatch.Stop();
        LogPhase("trackVisualResolve", phaseWatch.Elapsed);

        phaseWatch.Restart();
        StaticTrackTransform[] staticTransforms = TrackTransformResolver.ResolveStatic(level);
        phaseWatch.Stop();
        LogPhase("trackTransformResolve", phaseWatch.Elapsed);

        phaseWatch.Restart();
        NativeTileDimensions[] tileDimensions = TileDimensionsResolver.Resolve(level);
        phaseWatch.Stop();
        LogPhase("tileDimensionsResolve", phaseWatch.Elapsed);

        // TileDimensions changes FloorMesh's own length/width inputs. It cannot be
        // represented by a simple XY transform at corners: stretching the finished
        // 90-degree polygon would swap length/width on its outgoing arm. Keep the
        // common 100% geometry shared, and create extra cached geometry only for
        // distinct non-default dimension pairs actually present in the chart.
        phaseWatch.Restart();
        var sizedGeometries = geometrySnapshot.Geometries.ToList();
        var sizedPoints = geometrySnapshot.Points.ToList();
        var sizedGeometryIds = new Dictionary<SizedGeometryKey, uint>();
        int dimensionCount = Math.Min(floors.Length, tileDimensions.Length);
        for (int floor = 0; floor < dimensionCount; floor++)
        {
            NativeTileDimensions dimensions = tileDimensions[floor];
            if (Math.Abs(dimensions.Length - 1f) < 0.000001f &&
                Math.Abs(dimensions.Width - 1f) < 0.000001f)
                continue;

            ref NativeFloor target = ref floors[floor];
            var key = new SizedGeometryKey(
                target.GeometryId,
                (int)MathF.Round(dimensions.Length * 100_000f),
                (int)MathF.Round(dimensions.Width * 100_000f));
            if (!sizedGeometryIds.TryGetValue(key, out uint geometryId))
            {
                bool midSpin = floor < angles.Length && Math.Abs(angles[floor] - 999.0) < 0.000001;
                float entryAngle = target.EntryAngle;
                float exitAngle = GetExitAngle(floor, angles, entryAngle);
                float delta = Mod(exitAngle - entryAngle, TwoPi);
                FloorGeometry source = AdoFaiFloorGeometryBuilder.Get(
                    0f,
                    delta,
                    midSpin,
                    dimensions.Length,
                    dimensions.Width);

                geometryId = checked((uint)sizedGeometries.Count);
                uint pointOffset = checked((uint)sizedPoints.Count);
                foreach (System.Numerics.Vector2 point in source.Main)
                {
                    sizedPoints.Add(new NativePoint
                    {
                        X = point.X,
                        Y = point.Y
                    });
                }
                sizedGeometries.Add(new NativeGeometry
                {
                    PointOffset = pointOffset,
                    PointCount = checked((uint)source.Main.Length)
                });
                sizedGeometryIds.Add(key, geometryId);
            }
            target.GeometryId = geometryId;
        }
        phaseWatch.Stop();
        LogPhase("sizedGeometryPass", phaseWatch.Elapsed);

        phaseWatch.Restart();
        var iconAssets = new List<NativeIconAsset>();
        FloorIconResolution iconResolution = FloorIconResolver.ResolveAll(level, floors, iconAssets);
        phaseWatch.Stop();
        TimeSpan iconPassTime = phaseWatch.Elapsed;
        LogPhase("iconPass", iconPassTime);

        // PositionTrack is a persistent floor-state transform, not a runtime
        // MoveTrack tween. TileDimensions is already baked into the floor mesh
        // above, so transform scale remains available exclusively for PositionTrack
        // and MoveTrack and composes naturally with the shaped geometry.
        phaseWatch.Restart();
        int transformCount = Math.Min(floors.Length, staticTransforms.Length);
        var iconRotationOffsets = new float[floors.Length];
        for (int floor = 0; floor < transformCount; floor++)
        {
            StaticTrackTransform transform = staticTransforms[floor];
            iconRotationOffsets[floor] = transform.Rotation;
            ref NativeFloor target = ref floors[floor];
            target.X = transform.X;
            target.Y = transform.Y;
            target.EntryAngle += transform.Rotation;
            if (target.IconId != NativeFloor.NoIcon)
                target.IconAngle += transform.Rotation;
            target.TransformScaleX = transform.ScaleX;
            target.TransformScaleY = transform.ScaleY;
            target.TransformRotation = transform.Rotation;
            target.TransformOpacity = transform.Opacity;
            target.TrackTransformFlags = NativeFloor.TransformFlagEnabled |
                (transform.StickToFloors ? NativeFloor.TransformFlagStickToFloors : 0u);
            target.TrackExtendAnim = 1f;
            target.TrackSortingOffset = 0;
        }
        phaseWatch.Stop();
        TimeSpan transformPassTime = phaseWatch.Elapsed;
        LogPhase("transformPass", transformPassTime);

        phaseWatch.Restart();
        TerminalPortalResolver.Apply(floors, iconAssets);
        phaseWatch.Stop();
        TimeSpan terminalPortalTime = phaseWatch.Elapsed;
        LogPhase("terminalPortal", terminalPortalTime);

        // Track visual state has its own ABI fields now; icon flags stay icon-only.
        phaseWatch.Restart();
        int trackVisualCount = Math.Min(floors.Length, trackVisuals.Length);
        for (int floor = 0; floor < trackVisualCount; floor++)
        {
            NativeTrackVisual visual = trackVisuals[floor];
            ref NativeFloor target = ref floors[floor];
            target.TrackPrimaryColor = visual.PrimaryColor;
            target.TrackSecondaryColor = visual.SecondaryColor;
            target.TrackVisualFlags = visual.Flags;
            target.TrackAnimDuration = visual.AnimDuration;
            target.TrackGlowIntensity = visual.GlowIntensity;
            target.TrackStartFloor = visual.StartFloor;
            target.TrackPulseLength = visual.PulseLength;
        }
        phaseWatch.Stop();
        TimeSpan trackVisualPassTime = phaseWatch.Elapsed;
        LogPhase("trackVisualPass", trackVisualPassTime);

        TimeSpan iconTime = iconPassTime + transformPassTime + terminalPortalTime + trackVisualPassTime;

        phaseWatch.Restart();
        (float boundsLeft, float boundsTop, float boundsRight, float boundsBottom) =
            CalculateTransformBounds(floors, tileDimensions, geometrySnapshot);
        var snapshot = new NativeLevelSnapshot
        {
            Floors = floors,
            Geometries = sizedGeometries.ToArray(),
            Points = sizedPoints.ToArray(),
            IconAssets = iconAssets.ToArray(),
            CcwBeforeFloor = iconResolution.CcwBeforeFloor,
            IconRotationOffsets = iconRotationOffsets,
            BoundsLeft = boundsLeft,
            BoundsTop = boundsTop,
            BoundsRight = boundsRight,
            BoundsBottom = boundsBottom
        };
        phaseWatch.Stop();
        TimeSpan boundsFinalizeTime = phaseWatch.Elapsed;
        LogPhase("boundsFinalize", boundsFinalizeTime);

        totalWatch.Stop();
        LogPhase("snapshotTotal", totalWatch.Elapsed);

        NativeLevelSnapshotBuildMetrics baseMetrics = geometryResult.Metrics;
        return new NativeLevelSnapshotBuildResult(
            snapshot,
            new NativeLevelSnapshotBuildMetrics(
                baseMetrics.FloorGeometry,
                iconTime,
                baseMetrics.Finalize + boundsFinalizeTime,
                snapshot.Geometries.Length,
                snapshot.IconAssets.Length,
                iconResolution.ValidActionFloorCount));
    }

    private static void LogPhase(string phase, TimeSpan elapsed) =>
        Console.WriteLine($"[native-prepare] {phase}={elapsed.TotalMilliseconds:F1}ms");

    private static (float Left, float Top, float Right, float Bottom) CalculateTransformBounds(
        NativeFloor[] floors,
        NativeTileDimensions[] dimensions,
        NativeLevelSnapshot fallback)
    {
        if (floors.Length == 0)
            return (fallback.BoundsLeft, fallback.BoundsTop, fallback.BoundsRight, fallback.BoundsBottom);

        float left = float.PositiveInfinity;
        float top = float.PositiveInfinity;
        float right = float.NegativeInfinity;
        float bottom = float.NegativeInfinity;
        for (int i = 0; i < floors.Length; i++)
        {
            NativeFloor floor = floors[i];
            float sx = (floor.TrackTransformFlags & NativeFloor.TransformFlagEnabled) != 0u
                ? Math.Max(0.01f, Math.Abs(floor.TransformScaleX))
                : 1f;
            float sy = (floor.TrackTransformFlags & NativeFloor.TransformFlagEnabled) != 0u
                ? Math.Max(0.01f, Math.Abs(floor.TransformScaleY))
                : 1f;
            NativeTileDimensions dimension = (uint)i < (uint)dimensions.Length
                ? dimensions[i]
                : new NativeTileDimensions(1f, 1f);
            float dimensionRadius = Math.Max(0.5f, Math.Max(dimension.Length, dimension.Width));
            float margin = 1.25f * dimensionRadius * Math.Max(sx, sy);
            left = Math.Min(left, floor.X - margin);
            right = Math.Max(right, floor.X + margin);
            top = Math.Min(top, floor.Y - margin);
            bottom = Math.Max(bottom, floor.Y + margin);
        }
        return (left, top, right, bottom);
    }

    private static float GetExitAngle(int floor, double[] angles, float entryAngle)
    {
        if (floor >= angles.Length)
            return AddPi(entryAngle);
        if (Math.Abs(angles[floor] - 999.0) < 0.000001)
            return entryAngle;
        return LevelAngleToScreenRadians(angles[floor]);
    }

    private static float LevelAngleToScreenRadians(double angleDegrees)
    {
        double normalized = angleDegrees % 360.0;
        if (normalized < 0.0)
            normalized += 360.0;
        return (float)normalized * DegreesToRadians;
    }

    private static float AddPi(float angle)
    {
        float result = angle + MathF.PI;
        return result >= TwoPi ? result - TwoPi : result;
    }

    private static float Mod(float value, float modulus)
    {
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    private readonly record struct SizedGeometryKey(uint BaseGeometryId, int Length, int Width);
}
