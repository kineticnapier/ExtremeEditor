using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Core;

internal static class MissingTwirlIntervalProbeRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"extremeeditor-missing-twirl-interval-probe-{Guid.NewGuid():N}.adofai");

        try
        {
            File.WriteAllText(path, """
            {
              "angleData": [0, -120],
              "settings": {
                "bpm": 100,
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

            // Normal: 180° + 300° + 180° = 2.2 s.
            // If two missing Twirls bracket floor 1, only that floor is flipped:
            // 180° + 60° + 180° = 1.4 s, a maximum reduction of 0.8 s.
            Type probeType = typeof(LevelDocument).Assembly.GetType("ExtremeEditor.Core.MissingTwirlIntervalProbe")
                ?? throw new InvalidOperationException("MissingTwirlIntervalProbe is missing");
            MethodInfo analyze = probeType.GetMethod("Analyze", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("MissingTwirlIntervalProbe.Analyze is missing");
            object result = analyze.Invoke(null, [level, timing])
                ?? throw new InvalidOperationException("MissingTwirlIntervalProbe.Analyze returned null");

            Equal(1, ReadInt(result, "BestStartFloor"), "MissingTwirlIntervalProbe start floor");
            Equal(2, ReadInt(result, "BestEndFloorExclusive"), "MissingTwirlIntervalProbe end floor");
            Near(2.2, ReadDouble(result, "CurrentDurationSeconds"), "MissingTwirlIntervalProbe current duration");
            Near(1.4, ReadDouble(result, "BestDurationSeconds"), "MissingTwirlIntervalProbe best duration");
            Near(-0.8, ReadDouble(result, "DurationChangeSeconds"), "MissingTwirlIntervalProbe duration change");
            Near(0.8, ReadDouble(result, "MaximumReductionSeconds"), "MissingTwirlIntervalProbe maximum reduction");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static int ReadInt(object instance, string propertyName)
    {
        PropertyInfo property = instance.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"{instance.GetType().Name}.{propertyName} is missing");
        object? value = property.GetValue(instance);
        return value is int number
            ? number
            : throw new InvalidOperationException($"{instance.GetType().Name}.{propertyName} is not an int");
    }

    private static double ReadDouble(object instance, string propertyName)
    {
        PropertyInfo property = instance.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"{instance.GetType().Name}.{propertyName} is missing");
        object? value = property.GetValue(instance);
        return value is double number
            ? number
            : throw new InvalidOperationException($"{instance.GetType().Name}.{propertyName} is not a double");
    }

    private static void Equal(int expected, int actual, string name)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }

    private static void Near(double expected, double actual, string name)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > 0.00001)
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }
}
