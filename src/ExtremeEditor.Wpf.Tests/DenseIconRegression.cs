using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class DenseIconRegression
{
    public static void Run()
    {
        PropertyInfo enabledProperty = typeof(LevelViewport).GetProperty(
            "StaticSceneIconsEnabled",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "LevelViewport.StaticSceneIconsEnabled does not exist yet. Dense scenes must expose whether icon rendering remains enabled independently of candidate count.");

        var viewport = new LevelViewport();
        viewport.Measure(new Size(800, 600));
        viewport.Arrange(new Rect(0, 0, 800, 600));

        LevelDocument level = CreateDenseActionLevel(5_000);
        viewport.SetLevel(level, new SpatialGridIndex(level.Positions));
        Render(viewport);

        if (viewport.LastCandidateCount <= 2_000)
        {
            throw new InvalidOperationException(
                $"Dense icon regression requires more than 2000 candidates, actual={viewport.LastCandidateCount}.");
        }

        if (enabledProperty.GetValue(viewport) is not true)
        {
            throw new InvalidOperationException(
                "Event icons must remain enabled above 2000 visible candidates when zoom is above MinIconZoom.");
        }
    }

    private static LevelDocument CreateDenseActionLevel(int floorCount)
    {
        var positions = new Vector2[floorCount];
        const int columns = 100;
        const float spacing = 0.08f;
        for (int floor = 0; floor < floorCount; floor++)
        {
            positions[floor] = new Vector2(
                (floor % columns) * spacing,
                (floor / columns) * spacing);
        }

        return new LevelDocument
        {
            SourcePath = "<dense-icon-regression>",
            Angles = new double[Math.Max(0, floorCount - 1)],
            Positions = positions,
            ActionCount = 1,
            ActionTypeCounts = new Dictionary<string, int> { ["Twirl"] = 1 },
            ActionsByFloor = new Dictionary<int, LevelAction[]>
            {
                [0] = [new LevelAction(0, "Twirl", true, null, null, null, null)]
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
    }

    private static void Render(LevelViewport viewport)
    {
        MethodInfo onRender = typeof(LevelViewport).GetMethod(
            "OnRender",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LevelViewport.OnRender is missing.");

        var visual = new DrawingVisual();
        using DrawingContext drawingContext = visual.RenderOpen();
        onRender.Invoke(viewport, [drawingContext]);
    }
}
