using System.Numerics;
using System.Reflection;
using System.Windows;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class TemporalPlaybackRetentionRegression
{
    public static void Run()
    {
        VerifyPreloadedFloorSurvivesTimeWindowUntilSpatialEviction();
    }

    private static void VerifyPreloadedFloorSurvivesTimeWindowUntilSpatialEviction()
    {
        PropertyInfo retainedCountProperty = typeof(LevelViewport).GetProperty(
            "TemporalPlaybackRetainedFloorCount",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "LevelViewport.TemporalPlaybackRetainedFloorCount is missing.");

        MethodInfo renderTemporal = typeof(LevelViewport).GetMethod(
            "RenderTemporalPlaybackFloors",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "LevelViewport.RenderTemporalPlaybackFloors is missing.");

        FieldInfo cameraField = typeof(LevelViewport).GetField(
            "_camera",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport._camera is missing.");

        Vector2[] positions =
        [
            new Vector2(1_000f, 0f),
            new Vector2(20f, 0f),
            new Vector2(1_000f, 0f)
        ];

        var level = new LevelDocument
        {
            SourcePath = "<temporal-retention-regression>",
            Angles = new double[2],
            Positions = positions,
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = new Dictionary<int, LevelAction[]>(),
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

        var timingMap = new TimingMap(
        [
            new FloorTiming(0, 0.0, 0.01, 0, 0, Math.PI, 120, false, false),
            new FloorTiming(1, 0.1, 0.11, 0, 0, Math.PI, 120, false, false),
            new FloorTiming(2, 10.0, 10.01, 0, 0, Math.PI, 120, false, false)
        ]);

        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));

        try
        {
            // At zoom 28 an 800 px viewport spans roughly 28.6 world units.
            // x=20 is outside the real viewport centered at zero, but well inside
            // the agreed two-viewport preload/retention margin.
            cameraField.SetValue(viewport, Vector2.Zero);
            renderTemporal.Invoke(viewport, [timingMap, 0.1]);

            int preloaded = ReadRetainedCount(viewport, retainedCountProperty);
            if (preloaded != 1)
            {
                throw new InvalidOperationException(
                    $"An offscreen floor inside the two-viewport preload region must be retained. actual={preloaded}.");
            }
            if (viewport.TemporalPlaybackVisibleFloorCount != 0)
            {
                throw new InvalidOperationException(
                    $"Preloading must remain offscreen before the floor enters the real viewport. visible={viewport.TemporalPlaybackVisibleFloorCount}.");
            }

            // The floor has now left the temporal time window. Moving the camera
            // onto it must still reveal the retained floor instead of making it
            // pop into existence at the time-window boundary.
            cameraField.SetValue(viewport, new Vector2(20f, 0f));
            renderTemporal.Invoke(viewport, [timingMap, 2.0]);

            if (ReadRetainedCount(viewport, retainedCountProperty) != 1)
                throw new InvalidOperationException("A retained floor must survive after leaving the temporal time window while spatially relevant.");
            if (viewport.TemporalPlaybackVisibleFloorCount != 1)
            {
                throw new InvalidOperationException(
                    $"The retained floor must be visible when the camera reaches it. visible={viewport.TemporalPlaybackVisibleFloorCount}.");
            }

            // Once it is far outside the retention region, it can be discarded.
            cameraField.SetValue(viewport, new Vector2(200f, 0f));
            renderTemporal.Invoke(viewport, [timingMap, 2.0]);

            if (ReadRetainedCount(viewport, retainedCountProperty) != 0)
                throw new InvalidOperationException("A floor beyond the two-viewport retention region must be evicted.");
            if (viewport.TemporalPlaybackVisibleFloorCount != 0)
                throw new InvalidOperationException("An evicted offscreen floor must not be drawn.");
        }
        finally
        {
            viewport.ShutdownRasterWorker();
        }
    }

    private static int ReadRetainedCount(LevelViewport viewport, PropertyInfo property) =>
        (int)(property.GetValue(viewport)
            ?? throw new InvalidOperationException("TemporalPlaybackRetainedFloorCount returned null."));
}
