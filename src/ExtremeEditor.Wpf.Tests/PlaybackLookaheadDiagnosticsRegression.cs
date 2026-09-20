using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Windows;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class PlaybackLookaheadDiagnosticsRegression
{
    public static void Run()
    {
        VerifyDiagnosticsUiRefreshIsThrottled();
        VerifyFutureCameraSweepPreloadsFloorsBeforeTheyEnterViewport();
    }

    private static void VerifyDiagnosticsUiRefreshIsThrottled()
    {
        MethodInfo shouldRefresh = typeof(MainWindow).GetMethod(
            "ShouldRefreshPlaybackDiagnostics",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(long)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "MainWindow.ShouldRefreshPlaybackDiagnostics(long) is missing.");

        var window = new MainWindow();
        try
        {
            long start = Stopwatch.Frequency;
            bool first = (bool)(shouldRefresh.Invoke(window, [start])
                ?? throw new InvalidOperationException("ShouldRefreshPlaybackDiagnostics returned null."));
            bool after100ms = (bool)(shouldRefresh.Invoke(
                window,
                [start + Stopwatch.Frequency / 10])
                ?? throw new InvalidOperationException("ShouldRefreshPlaybackDiagnostics returned null."));
            bool after260ms = (bool)(shouldRefresh.Invoke(
                window,
                [start + Stopwatch.Frequency * 26 / 100])
                ?? throw new InvalidOperationException("ShouldRefreshPlaybackDiagnostics returned null."));

            if (!first)
                throw new InvalidOperationException("The first diagnostics UI sample must be rendered immediately.");
            if (after100ms)
                throw new InvalidOperationException("Diagnostics UI must not be rewritten again after only 100 ms.");
            if (!after260ms)
                throw new InvalidOperationException("Diagnostics UI must refresh after roughly 250 ms.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifyFutureCameraSweepPreloadsFloorsBeforeTheyEnterViewport()
    {
        Vector2[] positions =
        [
            Vector2.Zero,
            new Vector2(500f, 0f),
            new Vector2(1000f, 0f)
        ];

        var level = new LevelDocument
        {
            SourcePath = "<swept-preload-regression>",
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
            new FloorTiming(0, 0.0, 0.20, 0, 0, Math.PI, 120, false, false),
            new FloorTiming(1, 0.25, 0.35, 0, 0, Math.PI, 120, false, false),
            new FloorTiming(2, 0.49, 1.00, 0, 0, Math.PI, 120, false, false)
        ]);

        var viewport = new LevelViewport
        {
            FollowPlayer = true
        };
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));

        MethodInfo enterTemporal = typeof(LevelViewport).GetMethod(
            "EnterTemporalPlaybackMode",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.EnterTemporalPlaybackMode is missing.");
        MethodInfo setPlaybackFrame = typeof(LevelViewport).GetMethod(
            "SetPlaybackFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.SetPlaybackFrame is missing.");

        try
        {
            enterTemporal.Invoke(viewport, null);
            const double chartTime = 0.0;
            PlaybackPose pose = timingMap.GetPose(level, chartTime);
            setPlaybackFrame.Invoke(viewport, [timingMap, chartTime, pose]);

            if (viewport.TemporalPlaybackRetainedFloorCount < 3)
            {
                throw new InvalidOperationException(
                    $"Floors inside the current-to-future camera sweep must be preloaded before entering the real viewport. retained={viewport.TemporalPlaybackRetainedFloorCount}.");
            }

            if (viewport.TemporalPlaybackVisibleFloorCount != 1)
            {
                throw new InvalidOperationException(
                    $"Swept preload must not draw far-ahead floors before they enter the real viewport. visible={viewport.TemporalPlaybackVisibleFloorCount}.");
            }
        }
        finally
        {
            viewport.ShutdownRasterWorker();
        }
    }
}
