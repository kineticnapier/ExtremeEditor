using System.Numerics;
using System.Reflection;
using System.Windows;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class TemporalPlaybackIconDiagnosticsRegression
{
    public static void Run()
    {
        VerifyOnlyVisibleActionFloorsReachTemporalIconPath();
    }

    private static void VerifyOnlyVisibleActionFloorsReachTemporalIconPath()
    {
        PropertyInfo actionFloorCountProperty = typeof(LevelViewport).GetProperty(
            "TemporalPlaybackVisibleActionFloorCount",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "LevelViewport.TemporalPlaybackVisibleActionFloorCount is missing.");

        const int floorCount = 20;
        var positions = new Vector2[floorCount];
        positions[10] = Vector2.Zero;
        positions[11] = new Vector2(10_000f, 10_000f);

        LevelAction visibleAction = new(10, "Twirl", true, null, null, null, null);
        LevelAction offscreenAction = new(11, "Twirl", true, null, null, null, null);
        var level = new LevelDocument
        {
            SourcePath = "<temporal-icon-regression>",
            Angles = new double[floorCount - 1],
            Positions = positions,
            ActionCount = 2,
            ActionTypeCounts = new Dictionary<string, int> { ["Twirl"] = 2 },
            ActionsByFloor = new Dictionary<int, LevelAction[]>
            {
                [10] = [visibleAction],
                [11] = [offscreenAction]
            },
            InitialBpm = 120.0,
            SongFilename = null,
            OffsetMilliseconds = 0,
            PitchPercent = 100,
            CountdownTicks = 0,
            SeparateCountdownTime = false,
            DefaultHitSound = "Kick",
            HitSoundVolumePercent = 100,
            Bounds = PathBuilder.CalculateBounds(positions)
        };

        var timings = new FloorTiming[floorCount];
        for (int floor = 0; floor < floorCount; floor++)
        {
            double entry = floor * 0.01;
            timings[floor] = new FloorTiming(
                floor,
                entry,
                entry + 0.01,
                0,
                0,
                Math.PI,
                120,
                false,
                false);
        }

        var timingMap = new TimingMap(timings);
        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));

        try
        {
            MethodInfo renderTemporal = typeof(LevelViewport).GetMethod(
                "RenderTemporalPlaybackFloors",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    "LevelViewport.RenderTemporalPlaybackFloors is missing.");

            renderTemporal.Invoke(viewport, [timingMap, 0.105]);

            int visibleActionFloors = (int)(actionFloorCountProperty.GetValue(viewport)
                ?? throw new InvalidOperationException(
                    "TemporalPlaybackVisibleActionFloorCount returned null."));
            if (visibleActionFloors != 1)
            {
                throw new InvalidOperationException(
                    $"Only the on-screen action-bearing floor should reach temporal icon rendering. actual={visibleActionFloors}.");
            }
        }
        finally
        {
            viewport.ShutdownRasterWorker();
        }
    }
}
