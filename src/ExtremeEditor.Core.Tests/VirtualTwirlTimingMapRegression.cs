using System.Reflection;
using System.Runtime.CompilerServices;
using ExtremeEditor.Core;

internal static class VirtualTwirlTimingMapRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"extremeeditor-virtual-twirl-timing-{Guid.NewGuid():N}.adofai");

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
            TimingMap normal = TimingMapBuilder.Build(level);
            Near(2.2, normal.Duration, "Virtual Twirl baseline duration");

            MethodInfo build = typeof(TimingMapBuilder).GetMethod(
                "BuildWithVirtualTwirls",
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("TimingMapBuilder.BuildWithVirtualTwirls is missing");

            object result = build.Invoke(null, [level, new[] { 1, 2 }])
                ?? throw new InvalidOperationException("TimingMapBuilder.BuildWithVirtualTwirls returned null");
            if (result is not TimingMap corrected)
                throw new InvalidOperationException("TimingMapBuilder.BuildWithVirtualTwirls did not return TimingMap");

            // Virtual Twirls at floor 1 and floor 2 invert only floor 1.
            // 180° + 300° + 180° = 2.2 s becomes
            // 180° + 60° + 180° = 1.4 s.
            Near(1.4, corrected.Duration, "Virtual Twirl corrected duration");
            Near(0.2, corrected.Floors[1].ExitTime - corrected.Floors[1].EntryTime,
                "Virtual Twirl flipped floor duration");
            Near(0.6, corrected.Floors[2].ExitTime - corrected.Floors[2].EntryTime,
                "Virtual Twirl restored suffix duration");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void Near(double expected, double actual, string name)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > 0.00001)
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }
}
