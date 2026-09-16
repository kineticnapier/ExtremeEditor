using ExtremeEditor.Core;

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
    Console.WriteLine("MultiPlanet timing regression passed.");
    return 0;
}
finally
{
    if (File.Exists(path))
        File.Delete(path);
}

static void Near(double expected, double actual, string name)
{
    if (Math.Abs(expected - actual) > 0.00001)
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}
