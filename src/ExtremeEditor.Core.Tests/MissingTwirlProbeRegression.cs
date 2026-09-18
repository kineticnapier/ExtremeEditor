using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Core;

internal static class MissingTwirlProbeRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"extremeeditor-missing-twirl-probe-{Guid.NewGuid():N}.adofai");

        try
        {
            File.WriteAllText(path, """
            {
              "angleData": [0, 120],
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

            // Normal: 180° + 60° + 180° = 1.4 s.
            // A missing Twirl at floor 1 flips that suffix:
            // 180° + 300° + 180° = 2.2 s.
            const double targetDurationSeconds = 2.2;

            Type probeType = typeof(LevelDocument).Assembly.GetType("ExtremeEditor.Core.MissingTwirlProbe")
                ?? throw new InvalidOperationException("MissingTwirlProbe is missing");
            MethodInfo analyze = probeType.GetMethod("Analyze", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("MissingTwirlProbe.Analyze is missing");
            object result = analyze.Invoke(null, [level, timing, targetDurationSeconds, 5])
                ?? throw new InvalidOperationException("MissingTwirlProbe.Analyze returned null");

            Equal(1, ReadInt(result, "BestFloor"), "MissingTwirlProbe best floor");
            Near(1.4, ReadDouble(result, "CurrentDurationSeconds"), "MissingTwirlProbe current duration");
            Near(targetDurationSeconds, ReadDouble(result, "TargetDurationSeconds"), "MissingTwirlProbe target duration");
            Near(2.2, ReadDouble(result, "BestDurationSeconds"), "MissingTwirlProbe best duration");
            Near(0.0, ReadDouble(result, "BestAbsoluteErrorSeconds"), "MissingTwirlProbe best error");
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
