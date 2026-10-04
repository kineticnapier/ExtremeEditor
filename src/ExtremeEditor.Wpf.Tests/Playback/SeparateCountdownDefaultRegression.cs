using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SeparateCountdownDefaultRegression
{
    internal static void Run()
    {
        var failures = new List<string>();
        Check(failures, "normal loader missing-key stock default", () => VerifyOneLoader(LoadNormal, null, true));
        Check(failures, "normal loader explicit values", () => VerifyExplicitValues(LoadNormal));
        Check(failures, "streaming loader missing-key stock default", () => VerifyOneLoader(LoadStreaming, null, true));
        Check(failures, "streaming loader explicit values", () => VerifyExplicitValues(LoadStreaming));
        Check(failures, "flat streaming loader missing-key stock default", () => VerifyOneLoader(LoadFlat, null, true));
        Check(failures, "flat streaming loader explicit values", () => VerifyExplicitValues(LoadFlat));
        Check(failures, "three-loader consistency", VerifyLoaderConsistency);
        Check(failures, "missing-key chart/audio countdown mapping", VerifyMissingTimingSemantics);
        Check(failures, "explicit-false chart/audio countdown mapping", VerifyExplicitFalseTimingSemantics);
        Check(failures, "explicit source save preservation", VerifySavePreservation);
        Check(failures, "unrelated document state", VerifyUnrelatedState);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "separateCountdownTime stock default is not available:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(static failure => $"- {failure}")));
        }
    }

    private static void VerifyExplicitValues(Func<string, LevelDocument> loader)
    {
        VerifyOneLoader(loader, true, true);
        VerifyOneLoader(loader, false, false);
    }

    private static void VerifyOneLoader(
        Func<string, LevelDocument> loader,
        bool? sourceValue,
        bool expected)
    {
        string path = WriteFixture(sourceValue);
        try
        {
            LevelDocument document = loader(path);
            if (document.SeparateCountdownTime != expected)
            {
                string source = sourceValue?.ToString() ?? "missing";
                throw new InvalidOperationException(
                    $"source={source}: expected SeparateCountdownTime={expected}, actual={document.SeparateCountdownTime}.");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyLoaderConsistency()
    {
        foreach (bool? sourceValue in new bool?[] { null, true, false })
        {
            string path = WriteFixture(sourceValue);
            try
            {
                bool normal = LoadNormal(path).SeparateCountdownTime;
                bool streaming = LoadStreaming(path).SeparateCountdownTime;
                bool flat = LoadFlat(path).SeparateCountdownTime;
                if (normal != streaming || normal != flat)
                {
                    throw new InvalidOperationException(
                        $"source={sourceValue?.ToString() ?? "missing"}: normal={normal}, streaming={streaming}, flat={flat}.");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    private static void VerifyMissingTimingSemantics()
    {
        string path = WriteFixture(null);
        try
        {
            foreach ((string name, LevelDocument document) in LoadAll(path))
            {
                AssertNear(1.875, PlaybackClock.AudioToChartTime(document, 0.0), $"{name} missing audio->chart");
                AssertNear(0.0, PlaybackClock.ChartToAudioTime(document, 1.875), $"{name} missing chart->audio");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyExplicitFalseTimingSemantics()
    {
        string path = WriteFixture(false);
        try
        {
            foreach ((string name, LevelDocument document) in LoadAll(path))
            {
                AssertNear(-0.125, PlaybackClock.AudioToChartTime(document, 0.0), $"{name} false audio->chart");
                AssertNear(0.0, PlaybackClock.ChartToAudioTime(document, -0.125), $"{name} false chart->audio");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifySavePreservation()
    {
        foreach (bool sourceValue in new[] { true, false })
        {
            string path = WriteFixture(sourceValue);
            string savedPath = Path.ChangeExtension(path, ".saved.adofai");
            try
            {
                var session = new EditorSession(LoadNormal(path));
                session.SaveAsync(savedPath).GetAwaiter().GetResult();
                JsonObject saved = LooseAdoFaiJson.ParseObject(savedPath);
                JsonObject settings = saved["settings"] as JsonObject
                    ?? throw new InvalidOperationException("saved settings are missing.");
                if (settings["separateCountdownTime"]?.GetValue<bool>() != sourceValue)
                    throw new InvalidOperationException($"explicit {sourceValue} changed during save.");
            }
            finally
            {
                File.Delete(path);
                File.Delete(savedPath);
            }
        }
    }

    private static void VerifyUnrelatedState()
    {
        string path = WriteFixture(null);
        try
        {
            foreach ((string name, LevelDocument document) in LoadAll(path))
            {
                if (document.InitialBpm != 120.0 || document.OffsetMilliseconds != 125.0 ||
                    document.PitchPercent != 80.0 || document.CountdownTicks != 4 ||
                    document.SongFilename != "audio.ogg" || document.DefaultHitSound != "Hat" ||
                    document.HitSoundVolumePercent != 65.0 || document.ActionCount != 1 ||
                    document.DecorationCount != 1)
                {
                    throw new InvalidOperationException($"{name} changed unrelated audio/action/decoration state.");
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<(string Name, LevelDocument Document)> LoadAll(string path)
    {
        yield return ("normal", LoadNormal(path));
        yield return ("streaming", LoadStreaming(path));
        yield return ("flat", LoadFlat(path));
    }

    private static LevelDocument LoadNormal(string path) => AdoFaiLoader.Load(path).Document;
    private static LevelDocument LoadStreaming(string path) =>
        AdoFaiLoader.LoadAsync(path).GetAwaiter().GetResult().Document;
    private static LevelDocument LoadFlat(string path) =>
        AdoFaiLoader.LoadFlatAsync(path).GetAwaiter().GetResult().Document;

    private static string WriteFixture(bool? separateCountdownTime)
    {
        var settings = new JsonObject
        {
            ["songFilename"] = "audio.ogg",
            ["bpm"] = 120.0,
            ["volume"] = 73.0,
            ["offset"] = 125.0,
            ["pitch"] = 80.0,
            ["countdownTicks"] = 4,
            ["hitsound"] = "Hat",
            ["hitsoundVolume"] = 65.0,
            ["relativeTo"] = "Global",
            ["trackColor"] = "123456",
            ["futureSetting"] = "preserve"
        };
        if (separateCountdownTime is bool value)
            settings["separateCountdownTime"] = value;

        var root = new JsonObject
        {
            ["angleData"] = new JsonArray(0, 90),
            ["settings"] = settings,
            ["actions"] = new JsonArray(new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "Twirl",
                ["active"] = true
            }),
            ["decorations"] = new JsonArray(new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "AddDecoration",
                ["tag"] = "keep"
            })
        };
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-SeparateCountdown-{separateCountdownTime?.ToString() ?? "Missing"}-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private static void AssertNear(double expected, double actual, string label)
    {
        if (Math.Abs(expected - actual) > 0.000001)
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual}.");
    }

    private static void Check(List<string> failures, string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }
}
