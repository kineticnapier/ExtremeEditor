using System.Numerics;

namespace ExtremeEditor.Core;

public enum LevelActionKind : byte
{
    Unknown,
    Twirl,
    SetSpeed,
    MultiPlanet,
    Pause,
    SetHitsound,
    SetFloorIcon,
    Checkpoint
}

public static class LevelActionKinds
{
    public static LevelActionKind FromEventType(string eventType) => eventType switch
    {
        "Twirl" => LevelActionKind.Twirl,
        "SetSpeed" => LevelActionKind.SetSpeed,
        "MultiPlanet" => LevelActionKind.MultiPlanet,
        "Pause" => LevelActionKind.Pause,
        "SetHitsound" => LevelActionKind.SetHitsound,
        "SetFloorIcon" => LevelActionKind.SetFloorIcon,
        "Checkpoint" => LevelActionKind.Checkpoint,
        _ => LevelActionKind.Unknown
    };
}

public sealed record LevelAction(
    int Floor,
    string EventType,
    bool Active,
    string? SpeedType,
    double? BeatsPerMinute,
    double? BpmMultiplier,
    string? CustomIcon)
{
    public LevelActionKind Kind { get; init; } = LevelActionKinds.FromEventType(EventType);
    public double? SpeedRatio { get; set; }
    public string? HitSound { get; set; }
    public double? HitSoundVolumePercent { get; set; }
    public string? GameSound { get; set; }
    public string? Planets { get; set; }
    public double? AngleOffset { get; set; }
    public double? Duration { get; set; }
}

public sealed class LevelDocument
{
    private readonly object _actionStoreLock = new();
    private LevelActionStore? _actionStore;

    public required string SourcePath { get; init; }
    public required double[] Angles { get; init; }
    public required Vector2[] Positions { get; set; }
    public required int ActionCount { get; init; }
    public required IReadOnlyDictionary<string, int> ActionTypeCounts { get; init; }
    public required IReadOnlyDictionary<int, LevelAction[]> ActionsByFloor { get; init; }
    public LevelActionStore ActionStore
    {
        get
        {
            if (_actionStore is not null)
                return _actionStore;

            lock (_actionStoreLock)
            {
                _actionStore ??= LevelActionStore.FromDictionary(ActionsByFloor);
                return _actionStore;
            }
        }
        init => _actionStore = value;
    }
    public required double InitialBpm { get; init; }
    public required string? SongFilename { get; init; }
    public required double OffsetMilliseconds { get; init; }
    public required double PitchPercent { get; init; }
    public required int CountdownTicks { get; init; }
    public required bool SeparateCountdownTime { get; init; }
    public required string DefaultHitSound { get; init; }
    public required double HitSoundVolumePercent { get; init; }
    public required WorldRect Bounds { get; set; }

    public int FloorCount => Positions.Length;
    public IReadOnlyList<int> ActionFloors => ActionStore.Floors;

    public string? ResolveSongPath()
    {
        if (string.IsNullOrWhiteSpace(SongFilename) || SourcePath == "<synthetic>")
            return null;

        string? directory = Path.GetDirectoryName(SourcePath);
        return directory is null ? null : Path.GetFullPath(Path.Combine(directory, SongFilename));
    }

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
            ActionsByFloor = LevelActionStore.Empty.DictionaryView,
            ActionStore = LevelActionStore.Empty,
            InitialBpm = 100.0,
            SongFilename = null,
            OffsetMilliseconds = 0,
            PitchPercent = 100,
            CountdownTicks = 4,
            SeparateCountdownTime = true,
            DefaultHitSound = "Kick",
            HitSoundVolumePercent = 100,
            Bounds = PathBuilder.CalculateBounds(positions)
        };
    }
}
