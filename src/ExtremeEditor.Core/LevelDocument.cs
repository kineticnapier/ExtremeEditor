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
    public int SourceIndex { get; init; } = -1;
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
    private LevelActionStore? _actionStore;
    private IReadOnlyDictionary<int, LevelAction[]> _actionsByFloor =
        LevelActionStore.Empty.DictionaryView;

    public required string SourcePath { get; set; }
    public required double[] Angles { get; set; }
    public required Vector2[] Positions { get; set; }
    public required int ActionCount { get; set; }
    public required IReadOnlyDictionary<string, int> ActionTypeCounts { get; set; }
    public required IReadOnlyDictionary<int, LevelAction[]> ActionsByFloor
    {
        get => _actionsByFloor;
        set
        {
            _actionsByFloor = value ?? LevelActionStore.Empty.DictionaryView;
            _actionStore = null;
        }
    }
    public LevelActionStore ActionStore
    {
        get => _actionStore ??= LevelActionStore.FromDictionary(_actionsByFloor);
        set
        {
            _actionStore = value ?? LevelActionStore.Empty;
            _actionsByFloor = _actionStore.DictionaryView;
            ActionCount = _actionStore.ActionCount;
        }
    }
    public required double InitialBpm { get; set; }
    public required string? SongFilename { get; set; }
    public required double OffsetMilliseconds { get; set; }
    public required double PitchPercent { get; set; }
    public required int CountdownTicks { get; set; }
    public required bool SeparateCountdownTime { get; set; }
    public required string DefaultHitSound { get; set; }
    public required double HitSoundVolumePercent { get; set; }
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

    public void ReplaceActions(IEnumerable<LevelAction> actions)
    {
        LevelActionStore store = LevelActionStore.Create(actions);
        _actionStore = store;
        _actionsByFloor = store.DictionaryView;
        ActionCount = store.ActionCount;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (LevelAction action in store.Actions)
        {
            counts.TryGetValue(action.EventType, out int count);
            counts[action.EventType] = count + 1;
        }
        ActionTypeCounts = counts;
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
