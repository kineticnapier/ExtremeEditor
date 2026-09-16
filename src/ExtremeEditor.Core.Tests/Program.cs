using System.Reflection;
using ExtremeEditor.Core;

RunMultiPlanetRegression();
RunTimingProbeMidFloorSetSpeedRegression();
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
        object result = analyze.Invoke(null, [level, timing])
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

static void Near(double expected, double actual, string name)
{
    if (double.IsNaN(actual) || Math.Abs(expected - actual) > 0.00001)
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}
