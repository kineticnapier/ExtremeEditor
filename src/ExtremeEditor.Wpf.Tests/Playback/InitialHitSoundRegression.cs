using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using NAudio.Wave;

namespace ExtremeEditor.Wpf.Tests;

internal static class InitialHitSoundRegression
{
    public static void Run()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-InitialHitSound-{Guid.NewGuid():N}");
        string levelPath = Path.Combine(root, "level.adofai");
        string cacheRoot = Path.Combine(root, "AssetCache");

        try
        {
            Directory.CreateDirectory(root);
            WriteLevel(levelPath);

            LevelDocument level = AdoFaiLoader.Load(levelPath).Document;
            TimingMap timing = FlatTimingMapBuilder.Build(level);
            HitSoundTimeline timeline = HitSoundTimelineBuilder.Build(level);

            AssertState(timeline, 1, "Sizzle", 0.70);
            AssertState(timeline, 64, "Sizzle", 0.70);
            AssertState(timeline, 92, "Kick", 0.70);
            VerifyFloorOneIsScheduled(level, timing, timeline);

            WriteIncompleteCache(cacheRoot);
            AssetCacheStatus status = AssetSetupService.InspectCache(cacheRoot);
            if (status.IsReady ||
                status.Error is null ||
                !status.Error.Contains("Sizzle", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Initial hitsound cache validation did not diagnose missing required clip 'Sizzle'.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteLevel(string path)
    {
        double[] angles = new double[94];
        var fixture = new
        {
            angleData = angles,
            settings = new
            {
                bpm = 180,
                countdownTicks = 0,
                separateCountdownTime = false,
                offset = 0,
                pitch = 100,
                hitsound = "Kick",
                hitsoundVolume = 100
            },
            actions = new object[]
            {
                new
                {
                    floor = 1,
                    eventType = "SetHitsound",
                    gameSound = "Hitsound",
                    hitsound = "Sizzle",
                    hitsoundVolume = 70
                },
                new
                {
                    floor = 64,
                    eventType = "SetHitsound",
                    active = false,
                    gameSound = "Hitsound",
                    hitsound = "Kick",
                    hitsoundVolume = 100
                },
                new
                {
                    floor = 92,
                    eventType = "SetHitsound",
                    gameSound = "Hitsound",
                    hitsound = "Kick",
                    hitsoundVolume = 70
                }
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(fixture));
    }

    private static void AssertState(
        HitSoundTimeline timeline,
        int floor,
        string expectedName,
        double expectedVolume)
    {
        HitSoundState state = timeline.GetStateAtFloor(floor);
        if (!string.Equals(state.Name, expectedName, StringComparison.Ordinal) ||
            Math.Abs(state.Volume - expectedVolume) > 1e-9)
        {
            throw new InvalidOperationException(
                $"Initial hitsound timeline mismatch at floor {floor}: " +
                $"expected {expectedName} {expectedVolume:P0}, actual {state.Name} {state.Volume:P0}.");
        }
    }

    private static void VerifyFloorOneIsScheduled(
        LevelDocument level,
        TimingMap timing,
        HitSoundTimeline timeline)
    {
        Assembly audioAssembly = typeof(AudioPlayer).Assembly;
        Type renderedType = audioAssembly.GetType("ExtremeEditor.Audio.RenderedHitSound")
            ?? throw new InvalidOperationException("RenderedHitSound does not exist.");
        Type providerType = audioAssembly.GetType("ExtremeEditor.Audio.SampleAccurateHitSoundProvider")
            ?? throw new InvalidOperationException("SampleAccurateHitSoundProvider does not exist.");

        object sizzle = CreateRenderedClip(renderedType, amplitude: 0.25f);
        object kick = CreateRenderedClip(renderedType, amplitude: 0.50f);
        Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), renderedType);
        var clips = (IDictionary)(Activator.CreateInstance(dictionaryType)
            ?? throw new InvalidOperationException("Unable to create hitsound clip dictionary."));
        clips.Add("Sizzle", sizzle);
        clips.Add("Kick", kick);

        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);
        object provider = Activator.CreateInstance(
                providerType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [waveFormat, level, timing, timeline, clips, 120L * 44_100L],
                culture: null)
            ?? throw new InvalidOperationException("Unable to construct SampleAccurateHitSoundProvider.");

        FieldInfo scheduledHitsField = providerType.GetField(
            "_scheduledHits",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SampleAccurateHitSoundProvider schedule is not observable.");
        if (scheduledHitsField.GetValue(provider) is not IEnumerable scheduledHits)
            throw new InvalidOperationException("SampleAccurateHitSoundProvider schedule is invalid.");

        long floorOneFrame = checked((long)Math.Round(
            PlaybackClock.ChartToAudioTime(level, timing.GetEntryTime(1)) * 44_100,
            MidpointRounding.AwayFromZero));
        foreach (object hit in scheduledHits)
        {
            Type hitType = hit.GetType();
            long startFrame = (long)(hitType.GetProperty("StartFrame")?.GetValue(hit)
                ?? throw new InvalidOperationException("Scheduled hit StartFrame is missing."));
            if (startFrame != floorOneFrame)
                continue;

            object clip = hitType.GetProperty("Clip")?.GetValue(hit)
                ?? throw new InvalidOperationException("Scheduled hit Clip is missing.");
            float volume = (float)(hitType.GetProperty("Volume")?.GetValue(hit)
                ?? throw new InvalidOperationException("Scheduled hit Volume is missing."));
            if (!ReferenceEquals(clip, sizzle) || Math.Abs(volume - 0.70f) > 0.000001f)
            {
                throw new InvalidOperationException(
                    "Floor 1 must schedule the available Sizzle clip at 70% volume.");
            }
            return;
        }

        throw new InvalidOperationException(
            "Floor 1 must be scheduled when the Sizzle clip is available.");
    }

    private static object CreateRenderedClip(Type renderedType, float amplitude)
    {
        float[] samples = [amplitude, amplitude, amplitude, amplitude];
        return Activator.CreateInstance(
                   renderedType,
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   args: [samples, 0.0],
                   culture: null)
               ?? throw new InvalidOperationException("Unable to create RenderedHitSound.");
    }

    private static void WriteIncompleteCache(string cacheRoot)
    {
        string floorDirectory = Path.Combine(cacheRoot, "floor-mesh");
        string floorIconDirectory = Path.Combine(cacheRoot, "icons", "floors");
        string outlineDirectory = Path.Combine(cacheRoot, "icons", "outlines");
        string eventDirectory = Path.Combine(cacheRoot, "icons", "events");
        string categoryDirectory = Path.Combine(cacheRoot, "icons", "categories");
        string hitSoundDirectory = Path.Combine(cacheRoot, "hitsounds");

        foreach (string directory in new[]
                 {
                     floorDirectory,
                     floorIconDirectory,
                     outlineDirectory,
                     eventDirectory,
                     categoryDirectory,
                     hitSoundDirectory
                 })
        {
            Directory.CreateDirectory(directory);
        }

        foreach (string name in new[] { "tile.png", "perlin.png", "ramp.png", "glow.png" })
            File.WriteAllBytes(Path.Combine(floorDirectory, name), [1]);
        File.WriteAllBytes(Path.Combine(floorIconDirectory, "Rabbit.png"), [1]);
        File.WriteAllBytes(Path.Combine(outlineDirectory, "Rabbit.png"), [1]);
        File.WriteAllBytes(Path.Combine(eventDirectory, "SetSpeed.png"), [1]);
        File.WriteAllBytes(Path.Combine(categoryDirectory, "Gameplay.png"), [1]);

        foreach (string name in new[]
                 {
                     "sndClapHit.wav",
                     "sndHat.wav",
                     "sndKick.wav",
                     "sndReverbClap.wav",
                     "sndShaker.wav"
                 })
        {
            File.WriteAllBytes(Path.Combine(hitSoundDirectory, name), [1]);
        }

        var manifest = new
        {
            formatVersion = 2,
            gameVersion = "6000.3.10f1-test",
            sourceFingerprint = "current-source-fingerprint-test",
            floorIcons = 1,
            outlineIcons = 1,
            eventIcons = 1,
            categoryIcons = 1,
            hitSounds = 6,
            hitSoundNames = new[]
            {
                "ClapHit",
                "Hat",
                "Kick",
                "ReverbClap",
                "Shaker",
                "Sizzle"
            }
        };
        File.WriteAllText(
            Path.Combine(cacheRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest));
    }
}
