using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtremeEditor.Core;

public enum VfxRepeatType : byte
{
    Beat,
    Floor
}

public sealed record VfxRepeatDescriptor(
    int Floor,
    int SourceIndex,
    VfxRepeatType RepeatType,
    string? Tag,
    int? Repetitions,
    double? Interval,
    int? FloorCount,
    bool? ExecuteOnCurrentFloor,
    LevelAction SourceEvent);

public sealed record VfxOccurrence(
    int Floor,
    int SourceIndex,
    string EventType,
    bool Active,
    double? AngleOffset,
    double? Duration,
    string? Ease,
    string? EventTag,
    LevelAction SourceEvent,
    VfxRepeatDescriptor? RepeatDescriptor = null,
    double StartTime = 0,
    double StartEffectOffset = 0,
    int RepeatIteration = 0,
    double RepeatAngleOffset = 0,
    bool RepeatPlacementIsResolved = true,
    double? DurationSeconds = null)
{
    public bool IsRepeated => RepeatDescriptor is not null;

    // Compatibility name for consumers which describe the same state as a
    // derived occurrence rather than a repeated occurrence.
    public bool IsRepeatDerived => IsRepeated;

    public int? RepeatSourceIndex => RepeatDescriptor?.SourceIndex;
    public VfxRepeatType? RepeatType => RepeatDescriptor?.RepeatType;

    public bool? RepeatPlacementResolved => IsRepeated ? RepeatPlacementIsResolved : null;
}

public sealed record VfxTimeline(
    IReadOnlyList<VfxOccurrence> Occurrences,
    IReadOnlyList<VfxRepeatDescriptor> Repeats);

public static class VfxTimelineBuilder
{
    public static VfxTimeline Build(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        TimingMap timing = TimingMapBuilder.Build(level);
        double pitch = Math.Max(0.000001, level.PitchPercent * 0.01);
        double[] entryBeats = BuildEntryBeats(timing);
        var occurrences = new List<VfxOccurrence>();
        var repeats = new List<VfxRepeatDescriptor>();
        var repeatsByFloorAndTag = new Dictionary<(int Floor, string Tag), VfxRepeatDescriptor>();
        var sourceActions = new List<LevelAction>();

        foreach (LevelAction action in level.ActionStore.Actions)
        {
            if (string.Equals(action.EventType, "RepeatEvents", StringComparison.Ordinal))
            {
                if (action.Active &&
                    TryCreateRepeatDescriptor(action, out VfxRepeatDescriptor descriptor))
                {
                    repeats.Add(descriptor);
                    if (!string.IsNullOrEmpty(descriptor.Tag))
                    {
                        // Input order is stable within a floor. Assignment makes
                        // the later same-floor/tag definition authoritative.
                        foreach (string tag in SplitTags(descriptor.Tag))
                            repeatsByFloorAndTag[(descriptor.Floor, tag)] = descriptor;
                    }
                }
                continue;
            }

            sourceActions.Add(action);
        }

        var usedRepeats = new HashSet<VfxRepeatDescriptor>();
        foreach (LevelAction action in sourceActions)
        {
            VfxRepeatDescriptor? repeat = FindMatchingRepeat(action, repeatsByFloorAndTag);
            if (repeat is null)
            {
                occurrences.Add(CreateOccurrence(action, timing, pitch));
                continue;
            }

            usedRepeats.Add(repeat);
            ExpandOccurrences(action, repeat, timing, pitch, entryBeats, occurrences);
        }

        // GREEN-A exposed cross-floor tag association before concrete stock
        // expansion was known. Preserve that non-executable association shape
        // without treating it as a resolved RepeatEvents occurrence.
        foreach (VfxRepeatDescriptor repeat in repeats)
        {
            if (usedRepeats.Contains(repeat) || string.IsNullOrEmpty(repeat.Tag))
                continue;

            foreach (LevelAction source in sourceActions)
            {
                string? eventTag = MoveDecorationsTargeting.GetEventTag(source);
                if (source.Floor == repeat.Floor ||
                    !string.Equals(eventTag, repeat.Tag, StringComparison.Ordinal))
                {
                    continue;
                }

                occurrences.Add(CreateOccurrence(
                    source,
                    timing,
                    pitch,
                    repeatDescriptor: repeat,
                    repeatIteration: -1,
                    repeatAngleOffset: 0,
                    placementResolved: false));
            }
        }

        VfxOccurrence[] ordered = occurrences
            .OrderBy(static occurrence => occurrence.StartTime - occurrence.StartEffectOffset)
            .ThenBy(static occurrence => occurrence.Floor)
            .ToArray();
        return new VfxTimeline(ordered, repeats.ToArray());
    }

    private static void ExpandOccurrences(
        LevelAction source,
        VfxRepeatDescriptor repeat,
        TimingMap timing,
        double pitch,
        IReadOnlyList<double> entryBeats,
        List<VfxOccurrence> destination)
    {
        int count = repeat.RepeatType == VfxRepeatType.Beat
            ? Math.Max(0, repeat.Repetitions ?? 0)
            : Math.Max(0, repeat.FloorCount ?? 0);
        double interval = repeat.RepeatType == VfxRepeatType.Beat
            ? repeat.Interval ?? 0
            : -1;
        bool beatStyle = interval > 0;
        double originalAngleOffset = source.AngleOffset ?? 0;

        for (int iteration = 0; iteration <= count; iteration++)
        {
            int indexedFloor = source.Floor + (beatStyle ? 0 : iteration);
            if ((uint)indexedFloor >= (uint)timing.Floors.Count)
                break;

            int effectiveFloor = repeat.ExecuteOnCurrentFloor == true
                ? indexedFloor
                : source.Floor;
            double repeatAngleOffset;

            if (beatStyle)
            {
                repeatAngleOffset = interval * iteration * 180.0;
            }
            else if (repeat.ExecuteOnCurrentFloor == true)
            {
                repeatAngleOffset = 0;
            }
            else
            {
                repeatAngleOffset =
                    (entryBeats[indexedFloor] - entryBeats[source.Floor]) * 180.0;
            }

            double effectiveAngleOffset = originalAngleOffset + repeatAngleOffset;
            destination.Add(CreateOccurrence(
                source,
                timing,
                pitch,
                effectiveFloor,
                effectiveAngleOffset,
                iteration == 0 ? null : repeat,
                iteration,
                repeatAngleOffset,
                placementResolved: true));
        }
    }

    private static VfxOccurrence CreateOccurrence(
        LevelAction action,
        TimingMap timing,
        double pitch,
        int? effectiveFloor = null,
        double? effectiveAngleOffset = null,
        VfxRepeatDescriptor? repeatDescriptor = null,
        int repeatIteration = 0,
        double repeatAngleOffset = 0,
        bool placementResolved = true)
    {
        int floor = effectiveFloor ?? action.Floor;
        double? angleOffset = effectiveAngleOffset ?? action.AngleOffset;
        double startTime = ResolveStartTime(timing, floor, angleOffset ?? 0);
        double? durationSeconds = ResolveDurationSeconds(timing, floor, pitch, action.Duration);
        return new VfxOccurrence(
            floor,
            action.SourceIndex,
            action.EventType,
            action.Active,
            angleOffset,
            action.Duration,
            ReadString(MoveDecorationsTargeting.GetProperties(action), "ease"),
            MoveDecorationsTargeting.GetEventTag(action),
            action,
            RepeatDescriptor: repeatDescriptor,
            StartTime: startTime,
            StartEffectOffset: 0,
            RepeatIteration: repeatIteration,
            RepeatAngleOffset: repeatAngleOffset,
            RepeatPlacementIsResolved: placementResolved,
            DurationSeconds: durationSeconds);
    }

    private static VfxRepeatDescriptor? FindMatchingRepeat(
        LevelAction action,
        IReadOnlyDictionary<(int Floor, string Tag), VfxRepeatDescriptor> repeats)
    {
        string? eventTag = MoveDecorationsTargeting.GetEventTag(action);
        if (string.IsNullOrWhiteSpace(eventTag))
            return null;

        foreach (string tag in SplitTags(eventTag))
        {
            if (repeats.TryGetValue((action.Floor, tag), out VfxRepeatDescriptor? repeat))
                return repeat;
        }
        return null;
    }

    private static IEnumerable<string> SplitTags(string tags) =>
        tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static double ResolveStartTime(TimingMap timing, int floor, double degreeOffset)
    {
        if ((uint)floor >= (uint)timing.Floors.Count)
            return 0;

        FloorTiming floorTiming = timing.Floors[floor];
        double effectiveBpm = floorTiming.Bpm > 0 ? floorTiming.Bpm : 100.0;
        return floorTiming.EntryTime + degreeOffset / 180.0 * (60.0 / effectiveBpm);
    }

    private static double? ResolveDurationSeconds(
        TimingMap timing,
        int floor,
        double pitch,
        double? durationBeats)
    {
        if (durationBeats is not double duration ||
            (uint)floor >= (uint)timing.Floors.Count)
        {
            return null;
        }

        double effectiveBpm = timing.Floors[floor].Bpm > 0
            ? timing.Floors[floor].Bpm
            : 100.0;
        return duration * (60.0 / (effectiveBpm * pitch));
    }

    private static double[] BuildEntryBeats(TimingMap timing)
    {
        var entryBeats = new double[timing.Floors.Count];
        for (int floor = 1; floor < entryBeats.Length; floor++)
        {
            entryBeats[floor] = entryBeats[floor - 1] +
                                timing.Floors[floor - 1].AngleMoved / Math.PI;
        }
        return entryBeats;
    }

    private static bool TryCreateRepeatDescriptor(
        LevelAction action,
        out VfxRepeatDescriptor descriptor)
    {
        JsonObject? properties = action.PropertyOverrides;
        string? repeatTypeText = ReadString(properties, "repeatType");
        VfxRepeatType repeatType;
        if (string.Equals(repeatTypeText, "Beat", StringComparison.OrdinalIgnoreCase))
            repeatType = VfxRepeatType.Beat;
        else if (string.Equals(repeatTypeText, "Floor", StringComparison.OrdinalIgnoreCase))
            repeatType = VfxRepeatType.Floor;
        else
        {
            descriptor = null!;
            return false;
        }

        descriptor = new VfxRepeatDescriptor(
            action.Floor,
            action.SourceIndex,
            repeatType,
            MoveDecorationsTargeting.GetTargetTag(action) ??
                MoveDecorationsTargeting.GetEventTag(action),
            ReadInt(properties, "repetitions"),
            ReadDouble(properties, "interval"),
            ReadInt(properties, "floorCount"),
            ReadBool(properties, "executeOnCurrentFloor"),
            action);
        return true;
    }

    private static string? ReadString(JsonObject? properties, string name)
    {
        if (properties?[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out string? text))
            return text;
        return value.TryGetValue(out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static int? ReadInt(JsonObject? properties, string name)
    {
        if (properties?[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out int number))
            return number;
        if (value.TryGetValue(out long longNumber) && longNumber is >= int.MinValue and <= int.MaxValue)
            return (int)longNumber;
        return value.TryGetValue(out string? text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static double? ReadDouble(JsonObject? properties, string name)
    {
        if (properties?[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out double number))
            return number;
        return value.TryGetValue(out string? text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool? ReadBool(JsonObject? properties, string name)
    {
        if (properties?[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out bool boolean))
            return boolean;
        return value.TryGetValue(out string? text) && bool.TryParse(text, out boolean)
            ? boolean
            : null;
    }
}
