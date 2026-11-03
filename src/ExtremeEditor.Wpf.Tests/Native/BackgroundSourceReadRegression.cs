using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

// The background settings are a handful of values in the chart's "settings"
// object. Reading them must not build a DOM of the whole chart (16M-entry
// angleData on large charts), and each native preparation reads them once.
internal static class BackgroundSourceReadRegression
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExtremeEditor", "background-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            VerifyMatchesWholeDocumentRead(directory);
            VerifyNonFiniteBackgroundNumbersFallBack(directory);
            VerifyReadDoesNotScaleWithAngleData(directory);
            VerifyOneReadPerPreparation(directory);
            VerifyRepeatedReadReusesUnchangedContent(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string FullSettings =
        """
        {"version": 15, "backgroundColor": "102030", "bgImage": "bg.png", "bgImageColor": "aabbcc",
         "parallax": [40, 60.5], "bgDisplayMode": "Tiled", "imageSmoothing": false,
         "lockRot": true, "loopBG": true, "scalingRatio": "75.5", "levelDesc": "a"}
        """;

    private static void VerifyMatchesWholeDocumentRead(string directory)
    {
        (string Name, byte[] Bytes)[] cases =
        [
            ("settings after angleData", Utf8($$"""{"angleData": [0, 90, 180], "settings": {{FullSettings}}, "actions": []}""")),
            ("settings first", Utf8($$"""{"settings": {{FullSettings}}, "angleData": [0, 90], "actions": [], "decorations": []}""")),
            ("missing settings", Utf8("""{"angleData": [0, 90], "actions": []}""")),
            ("settings null", Utf8("""{"angleData": [0], "settings": null}""")),
            ("settings array", Utf8("""{"angleData": [0], "settings": [1, 2]}""")),
            ("number variants", Utf8("""{"angleData": [0], "settings": {"scalingRatio": 50, "parallax": [1, "x"], "imageSmoothing": "no"}}""")),
            ("nested settings key", Utf8($$$"""{"angleData": [0], "actions": [{"floor": 1, "eventType": "X", "settings": {"backgroundColor": "ffffff"}}], "settings": {{{FullSettings}}}}""")),
            ("trailing commas and comments", Utf8($$"""
                // chart
                {"angleData": [0, 90,], /* note */ "settings": {"backgroundColor": "445566", "bgImage": "b.png",}, "actions": [],}
                """)),
            ("utf8 bom", [0xEF, 0xBB, 0xBF, .. Utf8($$"""{"angleData": [0], "settings": {{FullSettings}}}""")]),
            ("utf16 bom", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes($$"""{"angleData": [0], "settings": {{FullSettings}}}""")]),
            ("raw control in string", Utf8("{\"angleData\": [0], \"settings\": {\"levelDesc\": \"a\tb\nc\", \"backgroundColor\": \"778899\"}}")),
            ("duplicate root settings", Utf8("""{"settings": {"backgroundColor": "111111"}, "angleData": [0], "settings": {"backgroundColor": "222222"}}"""))
        ];

        foreach ((string name, byte[] bytes) in cases)
        {
            string path = Path.Combine(directory, "case.adofai");
            File.WriteAllBytes(path, bytes);
            LevelDocument level = CreateLevel(path);

            BackgroundSourceState? expected = null;
            Exception? expectedError = null;
            try
            {
                expected = BackgroundSourceReader.ReadRoot(LooseAdoFaiJson.ParseObject(path)["settings"] as JsonObject);
            }
            catch (Exception ex)
            {
                expectedError = ex;
            }

            BackgroundSourceState? actual = null;
            Exception? actualError = null;
            try
            {
                actual = BackgroundSourceReader.ReadInitial(level);
            }
            catch (Exception ex)
            {
                actualError = ex;
            }

            if (expectedError is not null || actualError is not null)
            {
                if (expectedError?.GetType() != actualError?.GetType())
                {
                    throw new InvalidOperationException(
                        $"Background read '{name}' failure differs: expected={expectedError?.GetType().Name}, " +
                        $"actual={actualError?.GetType().Name} {actualError?.Message}.");
                }
                continue;
            }
            if (!expected.Equals(actual))
                throw new InvalidOperationException($"Background read '{name}' differs: expected={expected}, actual={actual}.");
        }

        if (BackgroundSourceReader.ReadInitial(CreateLevel(Path.Combine(directory, "missing.adofai"))) !=
            BackgroundSourceReader.ReadRoot(null))
            throw new InvalidOperationException("A missing chart file must keep the default background.");
    }

    private static void VerifyNonFiniteBackgroundNumbersFallBack(string directory)
    {
        string path = Path.Combine(directory, "nonfinite.adofai");
        File.WriteAllText(path, """
            {"settings":{"scalingRatio":"NaN","parallax":[1e100,-1e100]}}
            """);
        BackgroundSourceState initial = BackgroundSourceReader.ReadInitial(CreateLevel(path));
        if (initial.ScalingRatio != 100f || initial.ParallaxX != 100f || initial.ParallaxY != 100f)
            throw new InvalidOperationException($"Non-finite initial background settings were not rejected: {initial}.");

        File.WriteAllText(path, """
            {"settings":{"scalingRatio":1e100,"parallax":[25,75]}}
            """);
        BackgroundSourceState overflow = BackgroundSourceReader.ReadInitial(CreateLevel(path));
        if (overflow.ScalingRatio != 100f || overflow.ParallaxX != 25f || overflow.ParallaxY != 75f)
            throw new InvalidOperationException($"Overflowing initial background settings were not rejected: {overflow}.");

        JsonObject properties = JsonNode.Parse("""
            {"scalingRatio":"Infinity","parallax":[1e100,40]}
            """) as JsonObject ?? throw new InvalidOperationException("Invalid regression fixture.");
        BackgroundSourceState updated = BackgroundSourceReader.ApplyEvent(overflow, properties);
        if (updated.ScalingRatio != 100f || updated.ParallaxX != 100f || updated.ParallaxY != 40f)
            throw new InvalidOperationException($"Non-finite CustomBackground settings were not rejected: {updated}.");

        NativeBackgroundState native = BackgroundNativeEncoding.Encode(updated);
        if (!float.IsFinite(native.ScalingRatio) ||
            !float.IsFinite(native.ParallaxX) ||
            !float.IsFinite(native.ParallaxY))
            throw new InvalidOperationException("Non-finite background values reached the native renderer.");
    }

    private static void VerifyReadDoesNotScaleWithAngleData(string directory)
    {
        string path = Path.Combine(directory, "large.adofai");
        WriteLargeChart(path, floors: 1_000_000);
        long fileBytes = new FileInfo(path).Length;
        LevelDocument level = CreateLevel(path);
        BackgroundSourceReader.ReadInitial(level);

        long before = GC.GetAllocatedBytesForCurrentThread();
        BackgroundSourceState state = BackgroundSourceReader.ReadInitial(level);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (state.Color != "102030" || state.Image != "bg.png")
            throw new InvalidOperationException($"Large chart background was read incorrectly: {state}.");
        // Reading the file bytes is unavoidable; a DOM of angleData costs many
        // times the file size.
        if (allocated > fileBytes * 2)
        {
            throw new InvalidOperationException(
                $"Background settings read allocated {allocated / 1048576.0:F1}MiB for a " +
                $"{fileBytes / 1048576.0:F1}MiB chart; it parsed the whole document.");
        }
    }

    private static void VerifyOneReadPerPreparation(string directory)
    {
        string path = Path.Combine(directory, "prepare.adofai");
        WriteLargeChart(path, floors: 64);
        LevelDocument level = WpfLevelLoader.Load(path).Document;
        TimingMap timing = FlatTimingMapBuilder.Build(level);

        NativeLevelUpdateDiagnostics.EnableAndReset();
        long levelReads;
        long playbackReads;
        try
        {
            NativeLevelViewport.PrepareLevel(level);
            levelReads = NativeLevelUpdateDiagnostics.Read().BackgroundSourceReads;
            NativeLevelUpdateDiagnostics.EnableAndReset();
            NativeLevelViewport.PreparePlayback(level, timing);
            playbackReads = NativeLevelUpdateDiagnostics.Read().BackgroundSourceReads;
        }
        finally
        {
            NativeLevelUpdateDiagnostics.Disable();
        }

        if (levelReads != 1 || playbackReads != 1)
        {
            throw new InvalidOperationException(
                $"Native preparation reread the chart's background settings: " +
                $"level={levelReads}, playback={playbackReads} (expected 1 each).");
        }
    }

    // A repeated read of unchanged content skips the full JSON scan, while
    // any content change, even one that keeps the file size and lands within
    // the same file-time tick, is read again.
    private static void VerifyRepeatedReadReusesUnchangedContent(string directory)
    {
        string path = Path.Combine(directory, "repeat.adofai");
        WriteLargeChart(path, floors: 1_000_000);
        LevelDocument level = CreateLevel(path);
        BackgroundSourceReader.ReadInitial(level);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var scanWatch = System.Diagnostics.Stopwatch.StartNew();
        LooseAdoFaiJson.ParseRootProperty(path, "settings");
        scanWatch.Stop();
        watch.Restart();
        BackgroundSourceState cached = BackgroundSourceReader.ReadInitial(level);
        watch.Stop();
        if (cached.Color != "102030")
            throw new InvalidOperationException($"Repeated background read returned {cached}.");
        if (watch.Elapsed.TotalMilliseconds * 2 > scanWatch.Elapsed.TotalMilliseconds)
        {
            throw new InvalidOperationException(
                $"Repeated background read of unchanged content rescanned the chart: " +
                $"{watch.Elapsed.TotalMilliseconds:F1}ms vs scan {scanWatch.Elapsed.TotalMilliseconds:F1}ms.");
        }

        string text = File.ReadAllText(path);
        string changed = text.Replace("\"backgroundColor\": \"102030\"", "\"backgroundColor\": \"405060\"", StringComparison.Ordinal);
        if (changed.Length != text.Length || changed == text)
            throw new InvalidOperationException("Same-size fixture edit was not applied.");
        DateTime lastWrite = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, changed, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, lastWrite);
        BackgroundSourceState reread = BackgroundSourceReader.ReadInitial(level);
        if (reread.Color != "405060")
            throw new InvalidOperationException($"Same-size, same-time content change returned stale background {reread}.");
    }

    private static void WriteLargeChart(string path, int floors)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        writer.Write("{\"angleData\": [");
        for (int i = 0; i < floors; i++)
        {
            if (i > 0)
                writer.Write(", ");
            writer.Write((i % 4) * 90);
        }
        writer.Write("], \"settings\": ");
        writer.Write(FullSettings);
        writer.Write(", \"actions\": [], \"decorations\": []}");
    }

    private static LevelDocument CreateLevel(string path)
    {
        LevelDocument level = LevelDocument.CreateSynthetic(4);
        level.SourcePath = path;
        return level;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
