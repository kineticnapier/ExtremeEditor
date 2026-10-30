using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

/// <summary>
/// Guards parity between the dictionary-backed timing path used by export
/// and the ActionStore-backed path used by WPF playback.
/// </summary>
internal static class FlatTimingMapParityRegression
{
    public static void Run()
    {
        Verify("no angles", [], [], countdown: 0);
        Verify("one angle", [0], [], countdown: 4);
        Verify("high BPM", [157.5, -45], [], bpm: 8_192_000);
        Verify(
            "mixed stock events",
            [0, 999, 270, 90, 0, 180, 45, 999, 0],
            [
                new { floor = 0, eventType = "SetSpeed", speedType = "Bpm", beatsPerMinute = 180.0 },
                new { floor = 1, eventType = "MultiPlanet", planets = "ThreePlanets" },
                new { floor = 2, eventType = "Twirl" },
                new { floor = 3, eventType = "Pause", duration = 1.5 },
                new { floor = 3, eventType = "SetSpeed", speedType = "Multiplier", bpmMultiplier = 1.25 },
                new { floor = 4, eventType = "MultiPlanet", planets = "TwoPlanets" },
                new { floor = 5, eventType = "SetSpeed", speedType = "multiplier", bpmMultiplier = 0.75 },
                new { floor = 6, eventType = "Twirl", active = false },
                new { floor = 7, eventType = "Pause", duration = 0.5 },
                new { floor = 8, eventType = "SetSpeed", speedType = "Bpm", beatsPerMinute = 300.0 }
            ],
            countdown: 3);

        // Deterministic mixed-event charts catch future drift without relying on
        // external chart assets or nondeterministic random seeds.
        var random = new Random(104);
        double[] angleChoices = [0, 45, 90, 135, 180, 270, 999];
        for (int trial = 0; trial < 24; trial++)
        {
            double[] angles = Enumerable.Range(0, random.Next(2, 30))
                .Select(_ => angleChoices[random.Next(angleChoices.Length)])
                .ToArray();
            var actions = new List<object>();
            for (int floor = 0; floor <= angles.Length; floor++)
            {
                if (random.Next(3) != 0)
                    continue;

                actions.Add(random.Next(7) switch
                {
                    0 => (object)new { floor, eventType = "Twirl" },
                    1 => new { floor, eventType = "MultiPlanet", planets = "ThreePlanets" },
                    2 => new { floor, eventType = "MultiPlanet", planets = "TwoPlanets" },
                    3 => new { floor, eventType = "Pause", duration = 0.5 + random.Next(3) },
                    4 => new { floor, eventType = "SetSpeed", speedType = "Bpm", beatsPerMinute = 100.0 + random.Next(300) },
                    5 => new { floor, eventType = "SetSpeed", speedType = "Multiplier", bpmMultiplier = 0.5 + random.Next(4) * 0.25 },
                    _ => new { floor, eventType = "Twirl", active = false }
                });
            }

            Verify($"generated {trial}", angles, actions.ToArray(),
                bpm: 100 + random.Next(200), countdown: random.Next(5));
        }
    }

    private static void Verify(
        string label, double[] angles, object[] actions,
        double bpm = 120, int countdown = 0)
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"extremeeditor-flat-timing-parity-{Guid.NewGuid():N}.adofai");
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                angleData = angles,
                settings = new
                {
                    bpm,
                    offset = 0,
                    pitch = 100,
                    countdownTicks = countdown,
                    separateCountdownTime = false,
                    hitsound = "Kick",
                    hitsoundVolume = 100
                },
                actions
            });
            File.WriteAllText(path, json);

            LevelDocument level = AdoFaiLoader.Load(path).Document;
            TimingMap standard = TimingMapBuilder.Build(level);
            TimingMap flat = FlatTimingMapBuilder.Build(level);

            if (standard.Floors.Count != flat.Floors.Count)
                throw new InvalidOperationException(
                    $"{label}: floor count differs ({standard.Floors.Count} vs {flat.Floors.Count}).");

            Near(standard.Duration, flat.Duration, label, -1, "Duration");
            for (int floor = 0; floor < standard.Floors.Count; floor++)
            {
                FloorTiming expected = standard.Floors[floor];
                FloorTiming actual = flat.Floors[floor];
                if (expected.Floor != actual.Floor ||
                    expected.IsCcw != actual.IsCcw ||
                    expected.MidSpin != actual.MidSpin)
                    throw new InvalidOperationException(
                        $"{label}, floor {floor}: floor/direction/midspin differs.");

                Near(expected.EntryTime, actual.EntryTime, label, floor, "EntryTime");
                Near(expected.ExitTime, actual.ExitTime, label, floor, "ExitTime");
                Near(expected.EntryAngle, actual.EntryAngle, label, floor, "EntryAngle");
                Near(expected.ExitAngle, actual.ExitAngle, label, floor, "ExitAngle");
                Near(expected.AngleMoved, actual.AngleMoved, label, floor, "AngleMoved");
                Near(expected.Bpm, actual.Bpm, label, floor, "Bpm");
                Near(expected.PauseSeconds, actual.PauseSeconds, label, floor, "PauseSeconds");

                double midpoint = expected.EntryTime +
                    (expected.ExitTime - expected.EntryTime) * 0.5;
                PlaybackPose expectedPose = standard.GetPose(level, midpoint);
                PlaybackPose actualPose = flat.GetPose(level, midpoint);
                if (expectedPose.Floor != actualPose.Floor ||
                    expectedPose.StationaryIsRed != actualPose.StationaryIsRed ||
                    expectedPose.IsPreStart != actualPose.IsPreStart ||
                    expectedPose.StationaryPlanet != actualPose.StationaryPlanet ||
                    expectedPose.OrbitingPlanet != actualPose.OrbitingPlanet)
                    throw new InvalidOperationException(
                        $"{label}, floor {floor}: midpoint playback pose differs.");
                Near(expectedPose.Progress, actualPose.Progress, label, floor, "Pose.Progress");
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void Near(
        double expected, double actual, string label, int floor, string field)
    {
        double tolerance = 1e-9 * Math.Max(1.0, Math.Abs(expected));
        if (!double.IsFinite(expected) || !double.IsFinite(actual) ||
            Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException(
                $"{label}, floor {floor}, {field}: expected {expected:R}, actual {actual:R}.");
    }
}
