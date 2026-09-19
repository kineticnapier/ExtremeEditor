using System.Reflection;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class OpenLevelRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(LevelViewport).Assembly;
        Type loaderType = assembly.GetType("ExtremeEditor.Wpf.WpfLevelLoader")
            ?? throw new InvalidOperationException("WpfLevelLoader does not exist yet.");
        MethodInfo load = loaderType.GetMethod("Load", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WpfLevelLoader.Load is missing.");

        string path = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-WpfOpen-{Guid.NewGuid():N}.adofai");
        try
        {
            File.WriteAllText(path, """
            {
              "angleData": [0, 90, 180],
              "settings": { "bpm": 120, "offset": 0, "pitch": 100, "countdownTicks": 0, "separateCountdownTime": false, "hitsound": "Kick", "hitsoundVolume": 100 },
              "actions": [
                { "floor": 1, "eventType": "Twirl" },
                { "floor": 2, "eventType": "SetSpeed", "speedType": "Multiplier", "bpmMultiplier": 2.0 }
              ]
            }
            """);

            object result = load.Invoke(null, [path])
                ?? throw new InvalidOperationException("WpfLevelLoader.Load returned null.");
            PropertyInfo documentProperty = result.GetType().GetProperty("Document")
                ?? throw new InvalidOperationException("WPF level load result must expose Document.");
            PropertyInfo indexProperty = result.GetType().GetProperty("Index")
                ?? throw new InvalidOperationException("WPF level load result must expose Index.");

            if (documentProperty.GetValue(result) is not LevelDocument document)
                throw new InvalidOperationException("WPF level load result Document is invalid.");
            if (indexProperty.GetValue(result) is not SpatialGridIndex)
                throw new InvalidOperationException("WPF level load result Index is invalid.");
            if (document.FloorCount != 4 || document.ActionCount != 2)
                throw new InvalidOperationException($"Opened level mismatch: floors={document.FloorCount}, actions={document.ActionCount}.");
            if (!document.ActionsByFloor.TryGetValue(1, out LevelAction[]? actions) ||
                !actions.Any(action => action.EventType == "Twirl"))
                throw new InvalidOperationException("Opened level must preserve Twirl actions.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
