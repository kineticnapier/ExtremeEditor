using System.Numerics;

namespace ExtremeEditor.Core;

public sealed record LevelAction(
    int Floor,
    string EventType,
    bool Active,
    string? SpeedType,
    double? BeatsPerMinute,
    double? BpmMultiplier,
    string? CustomIcon)
{
    public double? SpeedRatio { get; set; }
}

public sealed class LevelDocument
{
    public required string SourcePath { get; init; }
    public required double[] Angles { get; init; }
    public required Vector2[] Positions { get; set; }
    public required int ActionCount { get; init; }
    public required IReadOnlyDictionary<string, int> ActionTypeCounts { get; init; }
    public required IReadOnlyDictionary<int, LevelAction[]> ActionsByFloor { get; init; }
    public required double InitialBpm { get; init; }
    public required WorldRect Bounds { get; set; }

    public int FloorCount => Positions.Length;

    public void RebuildGeometry()
    {
        Positions = PathBuilder.BuildPositions(Angles);
        Bounds = PathBuilder.CalculateBounds(Positions);
    }

    public static LevelDocument CreateSynthetic(int floors)
    {
        floors = Math.Max(1, floors);
        var angles = new double[Math.Max(0, floors - 1)];
        double angle = 0;
        for (int i = 0; i < angles.Length; i++)
        {
            if (i != 0 && (i & 255) == 0)
                angle += 22.5;
            angles[i] = angle;
        }

        var positions = PathBuilder.BuildPositions(angles);
        return new LevelDocument
        {
            SourcePath = "<synthetic>",
            Angles = angles,
            Positions = positions,
            ActionCount = 0,
            ActionTypeCounts = new Dictionary<string, int>(),
            ActionsByFloor = new Dictionary<int, LevelAction[]>(),
            InitialBpm = 100.0,
            Bounds = PathBuilder.CalculateBounds(positions)
        };
    }
}
