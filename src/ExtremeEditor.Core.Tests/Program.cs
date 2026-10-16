using System.Reflection;
using ExtremeEditor.Core;

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_MUSIC_TIMING_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.MusicTimingPointRegression.Run();
    Console.WriteLine("PASS: musical timing point grid regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_MUSIC_GRID_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.MusicBeatGridRegression.Run();
    Console.WriteLine("PASS: integer-sample music beat grid regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_SEARCH_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentDraftTimeSearchRegression.Run();
    Console.WriteLine("PASS: boundary-aware time draft binary search regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_DRAFT_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentTimeDraftRegression.Run();
    Console.WriteLine("PASS: time-first empty Segment draft editing regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_EMPTY_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentEmptyContentRegression.Run();
    Console.WriteLine("PASS: empty time-first Segment content and missing-entry regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_E2E_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentTogetherE2ERegression.Run();
    Console.WriteLine("PASS: two-client Git semantic sync to published .adofai export E2E regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_EXPORT_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentAdoFaiExportRegression.Run();
    Console.WriteLine("PASS: validated edited Segment .adofai export and roundtrip regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_EDITED_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentEditedCompositionRegression.Run();
    Console.WriteLine("PASS: edited Segment composition and sample-aligned ownership regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_BOUNDARY_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentBoundaryValidationRegression.Run();
    Console.WriteLine("PASS: floor-aligned Segment boundary timing/geometry/sample regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_GIT_REMOTE_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentGitRemoteSyncRegression.Run();
    Console.WriteLine("PASS: local bare Git remote Push/Fetch/semantic Pull regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_GIT_MERGE_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentGitBranchMergeRegression.Run();
    Console.WriteLine("PASS: Git history plus semantic Segment merge regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_GIT_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentGitWorkspaceRegression.Run();
    Console.WriteLine("PASS: isolated Git segment snapshot/branch regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_MERGE_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentTimeThreeWayMergeRegression.Run();
    Console.WriteLine("PASS: semantic three-way segment timeline merge regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_TIMELINE_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentTimePlannerRegression.Run();
    Console.WriteLine("PASS: sample-aligned segment timeline partition regression.");
    return 0;
}

if (string.Equals(
    Environment.GetEnvironmentVariable("EXTREMEEDITOR_SEGMENT_POC_ONLY"),
    "1", StringComparison.Ordinal))
{
    ExtremeEditor.Core.Tests.SegmentSplitMergeRegression.Run();
    Console.WriteLine("PASS: LevelDocument segment split/merge/repartition regression.");
    return 0;
}

RunMultiPlanetRegression();
RunMultiPlanetAfterMidSpinRegression();
RunTimingProbeMidFloorSetSpeedRegression();
RunArcExcessProbeRegression();
RunStockTimingProbeRegression();
RunStockTimingProbeHighBpmRegression();
ExtremeEditor.Core.Tests.ScaleRadiusPlaybackPoseRegression.Run();
Console.WriteLine("Core timing regressions passed.");
return 0;

static void RunMultiPlanetRegression()
{
    string path = Path.Combine(Path.GetTempPath(), $"extremeeditor-multiplanet-{Guid.NewGuid():N}.adofai");
    try
    {
        File.WriteAllText(path, """
        {
          "angleData": [0, 0, 0],
          "settings": {
            "bpm": 100,
            "offset": 0,
            "pitch": 100,
            "countdownTicks": 0,
            "separateCountdownTime": false,
            "hitsound": "Kick",
            "hitsoundVolume": 100
          },
          "actions": [
            {
              "floor": 1,
              "eventType": "MultiPlanet",
              "planets": "ThreePlanets"
            }
          ]
        }
        """);

        LevelDocument level = AdoFaiLoader.Load(path).Document;
        TimingMap timing = TimingMapBuilder.Build(level);

        // At 100 BPM a 180-degree two-planet step is 0.6 s.
        // ThreePlanets subtracts 60 degrees from the travel angle, so floor 1
        // should take 120/180 * 0.6 = 0.4 s. Floor 2 therefore starts at 1.0 s.
        Near(1.0, timing.GetEntryTime(2), "ThreePlanets timing at floor 2");
    }
    finally
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

static void RunMultiPlanetAfterMidSpinRegression()
{
    string path = Path.Combine(Path.GetTempPath(), $"extremeeditor-multiplanet-midspin-{Guid.NewGuid():N}.adofai");
    try
    {
        File.WriteAllText(path, """
        {
          "angleData": [0, 999, 270],
          "settings": {
            "bpm": 100,
            "offset": 0,
            "pitch": 100,
            "countdownTicks": 0,
            "separateCountdownTime": false,
            "hitsound": "Kick",
            "hitsoundVolume": 100
          },
          "actions": [
            {
              "floor": 2,
              "eventType": "MultiPlanet",
              "planets": "ThreePlanets"
            }
          ]
        }
        """);

        LevelDocument level = AdoFaiLoader.Load(path).Document;
        TimingMap timing = TimingMapBuilder.Build(level);
        FloorTiming floor = timing.Floors[2];

        // Stock has a special previous-midspin + three-planets correction.
        // This floor remains a 90-degree move (0.3 s at 100 BPM), rather than
        // receiving the ordinary 60-degree three-planet subtraction and becoming 30 degrees.
        Near(Math.PI / 2.0, floor.AngleMoved, "ThreePlanets after midspin angle");
        Near(0.3, floor.ExitTime - floor.EntryTime, "ThreePlanets after midspin duration");
    }
    finally
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

static void RunTimingProbeMidFloorSetSpeedRegression()
{
    string path = Path.Combine(Path.GetTempPath(), $"extremeeditor-timing-probe-{Guid.NewGuid():N}.adofai");
    try
    {
        File.WriteAllText(path, """
        {
          "angleData": [0, 0, 0],
          "settings": {
            "bpm": 100,
            "offset": 0,
            "pitch": 100,
            "countdownTicks": 0,
            "separateCountdownTime": false,
            "hitsound": "Kick",
            "hitsoundVolume": 100
          },
          "actions": [
            {
              "floor": 1,
              "eventType": "SetSpeed",
              "speedType": "Bpm",
              "beatsPerMinute": 200,
              "angleOffset": 90
            }
          ]
        }
        """);

        LevelDocument level = AdoFaiLoader.Load(path).Document;
        TimingMap timing = TimingMapBuilder.Build(level);
        LevelAction speed = level.ActionsByFloor[1].Single(action => action.EventType == "SetSpeed");

        PropertyInfo angleOffsetProperty = typeof(LevelAction).GetProperty("AngleOffset")
            ?? throw new InvalidOperationException("Timing probe prerequisite missing: LevelAction.AngleOffset");
        double? angleOffset = angleOffsetProperty.GetValue(speed) as double?;
        Near(90.0, angleOffset ?? double.NaN, "SetSpeed angleOffset parsing");

        Type probeType = typeof(LevelDocument).Assembly.GetType("ExtremeEditor.Core.TimingProbe")
            ?? throw new InvalidOperationException("TimingProbe is missing");
        MethodInfo analyze = probeType.GetMethod("Analyze", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("TimingProbe.Analyze is missing");
        object result = analyze.Invoke(null, [level, timing, 1e-7, 3])
            ?? throw new InvalidOperationException("TimingProbe.Analyze returned null");

        int? firstMismatchFloor = ReadNullableInt(result, "FirstMismatchFloor");
        if (firstMismatchFloor != 1)
            throw new InvalidOperationException($"Timing probe first mismatch: expected 1, actual {firstMismatchFloor?.ToString() ?? "null"}");

        Near(0.3, ReadDouble(result, "FirstMismatchCurrentSeconds"), "Timing probe current floor duration");
        Near(0.45, ReadDouble(result, "FirstMismatchReferenceSeconds"), "Timing probe reference floor duration");
    }
    finally
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

static void RunArcExcessProbeRegression()
{
    string path = Path.Combine(Path.GetTempPath(), $"extremeeditor-arc-excess-probe-{Guid.NewGuid():N}.adofai");
    try
    {
        File.WriteAllText(path, """
        {
          "angleData": [120, 0, 120, 0],
          "settings": {
            "bpm": 100,
            "offset": 0,
            "pitch": 100,
            "countdownTicks": 0,
            "separateCountdownTime": false,
            "hitsound": "Kick",
            "hitsoundVolume": 100
          },
          "actions": [
            {
              "floor": 2,
              "eventType": "SetSpeed",
              "speedType": "Bpm",
              "beatsPerMinute": 200
            }
          ]
        }
        """);

        LevelDocument level = AdoFaiLoader.Load(path).Document;
        TimingMap timing = TimingMapBuilder.Build(level);

        Type probeType = typeof(LevelDocument).Assembly.GetType("ExtremeEditor.Core.TimingProbe")
            ?? throw new InvalidOperationException("TimingProbe is missing");
        MethodInfo analyze = probeType.GetMethod("AnalyzeArcExcess", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("TimingProbe.AnalyzeArcExcess is missing");
        object result = analyze.Invoke(null, [level, timing])
            ?? throw new InvalidOperationException("TimingProbe.AnalyzeArcExcess returned null");

        object[] intervals = ReadEnumerable(result, "Intervals");
        if (intervals.Length != 2)
            throw new InvalidOperationException($"Arc-excess interval count: expected 2, actual {intervals.Length}");

        Equal(0, ReadInt(intervals[0], "StartFloor"), "Arc-excess first interval start");
        Equal(1, ReadInt(intervals[0], "EndFloor"), "Arc-excess first interval end");
        Equal(1, ReadInt(intervals[0], "LongArcFloorCount"), "Arc-excess first interval long arcs");
        Near(0.8, ReadDouble(intervals[0], "ExcessSeconds"), "Arc-excess first interval excess");

        Equal(2, ReadInt(intervals[1], "StartFloor"), "Arc-excess second interval start");
        Equal(4, ReadInt(intervals[1], "EndFloor"), "Arc-excess second interval end");
        Equal(1, ReadInt(intervals[1], "LongArcFloorCount"), "Arc-excess second interval long arcs");
        Near(0.4, ReadDouble(intervals[1], "ExcessSeconds"), "Arc-excess second interval excess");

        Near(1.2, ReadDouble(result, "TotalExcessSeconds"), "Arc-excess total excess");
    }
    finally
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

static void RunStockTimingProbeRegression()
{
    string path = Path.Combine(Path.GetTempPath(), $"extremeeditor-stock-timing-probe-{Guid.NewGuid():N}.adofai");
    try
    {
        File.WriteAllText(path, """
        {
          "angleData": [0, 0, 0],
          "settings": {
            "bpm": 100,
            "offset": 0,
            "pitch": 100,
            "countdownTicks": 0,
            "separateCountdownTime": false,
            "hitsound": "Kick",
            "hitsoundVolume": 100
          },
          "actions": [
            {
              "floor": 1,
              "eventType": "SetSpeed",
              "speedType": "Bpm",
              "beatsPerMinute": 200,
              "angleOffset": 90
            }
          ]
        }
        """);

        LevelDocument level = AdoFaiLoader.Load(path).Document;
        TimingMap timing = TimingMapBuilder.Build(level);

        Type probeType = typeof(LevelDocument).Assembly.GetType("ExtremeEditor.Core.StockTimingProbe")
            ?? throw new InvalidOperationException("StockTimingProbe is missing");
        MethodInfo analyze = probeType.GetMethod("Analyze", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("StockTimingProbe.Analyze is missing");
        object result = analyze.Invoke(null, [level, timing, 1e-7, 3])
            ?? throw new InvalidOperationException("StockTimingProbe.Analyze returned null");

        int? firstMismatchFloor = ReadNullableInt(result, "FirstMismatchFloor");
        if (firstMismatchFloor != 1)
            throw new InvalidOperationException($"Stock timing probe first mismatch: expected 1, actual {firstMismatchFloor?.ToString() ?? "null"}");

        Near(0.3, ReadDouble(result, "FirstMismatchCurrentSeconds"), "Stock timing probe current floor duration");
        Near(0.45, ReadDouble(result, "FirstMismatchStockSeconds"), "Stock timing probe stock floor duration");
    }
    finally
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

static void RunStockTimingProbeHighBpmRegression()
{
    string path = Path.Combine(Path.GetTempPath(), $"extremeeditor-stock-timing-probe-high-bpm-{Guid.NewGuid():N}.adofai");
    try
    {
        File.WriteAllText(path, """
        {
          "angleData": [157.5, -45],
          "settings": {
            "bpm": 8192000,
            "offset": 0,
            "pitch": 100,
            "countdownTicks": 0,
            "separateCountdownTime": false,
            "hitsound": "Kick",
            "hitsoundVolume": 100
          },
          "actions": []
        }
        """);

        LevelDocument level = AdoFaiLoader.Load(path).Document;
        TimingMap timing = TimingMapBuilder.Build(level);
        StockTimingProbeResult result = StockTimingProbe.Analyze(level, timing, 1e-7, 3);

        if (result.FirstMismatchFloor is int mismatchFloor)
            throw new InvalidOperationException($"Stock timing probe high-BPM false turnaround at floor {mismatchFloor}");
    }
    finally
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

static object[] ReadEnumerable(object instance, string propertyName)
{
    PropertyInfo property = instance.GetType().GetProperty(propertyName)
        ?? throw new InvalidOperationException($"{instance.GetType().Name}.{propertyName} is missing");
    object? value = property.GetValue(instance);
    return value is System.Collections.IEnumerable enumerable
        ? enumerable.Cast<object>().ToArray()
        : throw new InvalidOperationException($"{instance.GetType().Name}.{propertyName} is not enumerable");
}

static int ReadInt(object instance, string propertyName)
{
    PropertyInfo property = instance.GetType().GetProperty(propertyName)
        ?? throw new InvalidOperationException($"{instance.GetType().Name}.{propertyName} is missing");
    object? value = property.GetValue(instance);
    return value is int number
        ? number
        : throw new InvalidOperationException($"{instance.GetType().Name}.{propertyName} is not an int");
}

static double ReadDouble(object instance, string propertyName)
{
    PropertyInfo property = instance.GetType().GetProperty(propertyName)
        ?? throw new InvalidOperationException($"TimingProbeResult.{propertyName} is missing");
    object? value = property.GetValue(instance);
    return value is double number
        ? number
        : throw new InvalidOperationException($"TimingProbeResult.{propertyName} is not a double");
}

static int? ReadNullableInt(object instance, string propertyName)
{
    PropertyInfo property = instance.GetType().GetProperty(propertyName)
        ?? throw new InvalidOperationException($"TimingProbeResult.{propertyName} is missing");
    object? value = property.GetValue(instance);
    return value switch
    {
        null => null,
        int number => number,
        _ => throw new InvalidOperationException($"TimingProbeResult.{propertyName} is not an int?")
    };
}

static void Equal(int expected, int actual, string name)
{
    if (expected != actual)
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}

static void Near(double expected, double actual, string name)
{
    if (double.IsNaN(actual) || Math.Abs(expected - actual) > 0.00001)
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}
