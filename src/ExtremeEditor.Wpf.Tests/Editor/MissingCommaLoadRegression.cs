using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

// Stock ADOFAI parses levels with GDMiniJSON, whose arrays and objects skip
// commas between members instead of requiring them. ExtremeEditor supplies a
// missing comma only where one value has ended and the next begins an
// object, array or string, outside strings and comments, and re-validates
// the result strictly; every other syntax error is still rejected.
internal static class MissingCommaLoadRegression
{
    private const string Settings = "\"settings\": { \"version\": 15, \"bpm\": 120 }";

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExtremeEditor", "missing-comma-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            VerifyValidChartIsUntouched(directory);
            VerifyMissingCommaBetweenEvents(directory);
            VerifyStringsAndCommentsAreNotRepaired();
            VerifyRawStringControlsAfterQuotedComments(directory);
            VerifyCombinedCommaAndRawControlRepairs(directory);
            VerifyCarriageReturnTerminatedComments(directory);
            VerifyUnrelatedErrorsAreRejected(directory);
            VerifySaveKeepsEveryEvent(directory);
            VerifyPhantigmaIfPresent(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyValidChartIsUntouched(string directory)
    {
        byte[] valid = Utf8($$"""{"angleData": [0, 90, 180], {{Settings}}, "actions": [{"floor": 1, "eventType": "Twirl"}], "decorations": []}""");
        byte[] normalized = LooseAdoFaiJson.NormalizeToUtf8(valid, out IReadOnlyList<JsonRepair> repairs);
        Require(ReferenceEquals(valid, normalized) && repairs.Count == 0,
            "A strictly valid chart was rewritten by the missing-comma repair.");

        string path = Path.Combine(directory, "valid.adofai");
        File.WriteAllBytes(path, valid);
        WpfLevelLoadResult loaded = WpfLevelLoader.Load(path);
        Require(loaded.JsonRepairs.Count == 0 && loaded.Document.ActionCount == 1,
            "A strictly valid chart reported repairs.");
    }

    private static void VerifyMissingCommaBetweenEvents(string directory)
    {
        string broken = """
            {"angleData": [0, 90, 180, 270], "settings": { "version": 15, "bpm": 120 }, "actions": [
                { "floor": 1, "eventType": "Twirl" }

                { "floor": 2, "eventType": "SetSpeed", "speedType": "Bpm", "beatsPerMinute": 240, "customField": "a } { b" }
                { "floor": 3, "eventType": "Flash", "duration": 1, "startColor": "ff0000" }
            ], "decorations": []}
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
        string fixedText = broken
            .Replace("\"Twirl\" }\n", "\"Twirl\" },\n", StringComparison.Ordinal)
            .Replace("\"a } { b\" }\n", "\"a } { b\" },\n", StringComparison.Ordinal);
        Require(fixedText != broken, "Fixture could not build the comma-corrected chart.");

        string brokenPath = Path.Combine(directory, "broken.adofai");
        string fixedPath = Path.Combine(directory, "fixed.adofai");
        File.WriteAllText(brokenPath, broken, new UTF8Encoding(false));
        File.WriteAllText(fixedPath, fixedText, new UTF8Encoding(false));
        byte[] originalBytes = File.ReadAllBytes(brokenPath);

        WpfLevelLoadResult repaired = WpfLevelLoader.Load(brokenPath);
        WpfLevelLoadResult reference = WpfLevelLoader.Load(fixedPath);
        Require(repaired.Document.FloorCount == reference.Document.FloorCount &&
                repaired.Document.ActionCount == reference.Document.ActionCount &&
                repaired.Document.ActionCount == 3,
            $"Repaired chart differs from the hand-fixed one: floors {repaired.Document.FloorCount}/{reference.Document.FloorCount}, " +
            $"actions {repaired.Document.ActionCount}/{reference.Document.ActionCount}.");
        Require(repaired.JsonRepairs.Count == 2 &&
                repaired.JsonRepairs[0] is { Line: 2, Column: 40 } &&
                repaired.JsonRepairs[1].Line == 4,
            "Repairs were not reported at the two missing commas: " +
            string.Join(", ", repaired.JsonRepairs.Select(static r => $"{r.Line}:{r.Column}")));
        Require(File.ReadAllBytes(brokenPath).AsSpan().SequenceEqual(originalBytes),
            "Loading a repaired chart rewrote the source file.");
    }

    private static void VerifyStringsAndCommentsAreNotRepaired()
    {
        // Every candidate pattern sits inside a string or comment; the file is
        // valid as it stands and must come back byte for byte.
        byte[] tricky = Utf8("""
            {"angleData": [0], "settings": {"levelDesc": "} {  \"x\" \"y\"  ] ["},
             // } { "a" "b"
             /* ] [ "c" { */
             "actions": [], "decorations": []}
            """);
        byte[] normalized = LooseAdoFaiJson.NormalizeToUtf8(tricky, out IReadOnlyList<JsonRepair> repairs);
        Require(ReferenceEquals(tricky, normalized) && repairs.Count == 0,
            "Commas were inserted inside strings or comments.");
    }

    private static void VerifyRawStringControlsAfterQuotedComments(string directory)
    {
        // A quote in a comment is not a string delimiter. If a later JSON
        // string contains a raw newline, the normalizer must escape that
        // newline without changing the comment or the source chart.
        string[] comments =
        [
            "// unmatched \" in a line comment",
            "/* unmatched \" in a block comment */"
        ];
        for (int i = 0; i < comments.Length; i++)
        {
            string source = "{\n  " + comments[i] +
                "\n  \"angleData\": [0, 90]," +
                "\n  \"settings\": {\"version\": 15, \"bpm\": 120, \"levelDesc\": \"first\nsecond\"}," +
                "\n  \"actions\": [], \"decorations\": []\n}";
            byte[] original = Utf8(source);
            byte[] normalized = LooseAdoFaiJson.NormalizeToUtf8(
                original, out IReadOnlyList<JsonRepair> repairs);
            Require(repairs.Count == 0 &&
                    Encoding.UTF8.GetString(normalized).Contains(
                        "\"first\\nsecond\"", StringComparison.Ordinal),
                $"Raw newline after quoted comment {i} was not escaped.");

            string path = Path.Combine(directory, $"raw-comment-{i}.adofai");
            File.WriteAllBytes(path, original);
            JsonObject parsed = LooseAdoFaiJson.ParseObject(path);
            JsonNode? streamed = LooseAdoFaiJson.ParseRootProperty(original, "settings");
            Require(parsed["settings"]?["levelDesc"]?.GetValue<string>() == "first\nsecond" &&
                    streamed?["levelDesc"]?.GetValue<string>() == "first\nsecond",
                $"Quoted comment {i} corrupted the parsed description.");
            Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(original),
                "Normalizing raw string controls modified the source chart.");
        }
    }

    private static void VerifyCombinedCommaAndRawControlRepairs(string directory)
    {
        // A missing comma and a raw newline can occur in the same chart.
        // Unmatched quotes in both comment forms must not confuse either repair.
        string source =
            "{\n  // unmatched \" in a line comment\n" +
            "  \"angleData\": [0, 90, 180],\n" +
            "  \"settings\": {\"version\": 15, \"bpm\": 120, \"levelDesc\": \"line one\nline two\"},\n" +
            "  \"actions\": [{\"floor\": 1, \"eventType\": \"Twirl\"}\n" +
            "    /* unmatched \" in a block comment */\n" +
            "    {\"floor\": 2, \"eventType\": \"Twirl\"}],\n" +
            "  \"decorations\": []\n}";
        byte[] original = Utf8(source);
        byte[] normalized = LooseAdoFaiJson.NormalizeToUtf8(
            original, out IReadOnlyList<JsonRepair> repairs);
        Require(repairs.Count == 1 &&
                Encoding.UTF8.GetString(normalized).Contains(
                    "\"line one\\nline two\"", StringComparison.Ordinal),
            "Combined comma and raw-newline repairs did not both apply.");

        string path = Path.Combine(directory, "combined-repairs.adofai");
        File.WriteAllBytes(path, original);
        JsonObject parsed = LooseAdoFaiJson.ParseObject(path);
        JsonNode? streamed = LooseAdoFaiJson.ParseRootProperty(original, "actions");
        Require(parsed["actions"] is JsonArray { Count: 2 } &&
                streamed is JsonArray { Count: 2 } &&
                parsed["settings"]?["levelDesc"]?.GetValue<string>() == "line one\nline two",
            "Combined repairs corrupted the parsed chart or streamed actions.");
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(original),
            "Combined repairs modified the original chart.");
    }

    private static void VerifyCarriageReturnTerminatedComments(string directory)
    {
        // JSON line comments end at CR as well as LF. A CR-only chart can
        // contain both a missing comma and an unescaped CR in a string.
        string source =
            "{\r  // unmatched \" in a CR-terminated comment\r" +
            "  \"angleData\": [0, 90, 180],\r" +
            "  \"settings\": {\"version\": 15, \"bpm\": 120, \"levelDesc\": \"first\rsecond\"},\r" +
            "  \"actions\": [{\"floor\": 1, \"eventType\": \"Twirl\"}\r" +
            "    // unmatched \" before the next event\r" +
            "    {\"floor\": 2, \"eventType\": \"Twirl\"}],\r" +
            "  \"decorations\": []\r}";
        byte[] original = Utf8(source);
        byte[] normalized = LooseAdoFaiJson.NormalizeToUtf8(
            original, out IReadOnlyList<JsonRepair> repairs);
        Require(repairs.Count == 1 && repairs[0].Line == 6 &&
                Encoding.UTF8.GetString(normalized).Contains(
                    "\"first\\rsecond\"", StringComparison.Ordinal),
            "CR-terminated comments hid a missing comma, reported the wrong line or missed a raw string CR.");

        string path = Path.Combine(directory, "cr-only-comments.adofai");
        File.WriteAllBytes(path, original);
        JsonObject parsed = LooseAdoFaiJson.ParseObject(path);
        JsonNode? streamed = LooseAdoFaiJson.ParseRootProperty(original, "actions");
        Require(parsed["actions"] is JsonArray { Count: 2 } &&
                streamed is JsonArray { Count: 2 } &&
                parsed["settings"]?["levelDesc"]?.GetValue<string>() == "first\rsecond",
            "CR-only comment termination corrupted the parsed chart.");
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(original),
            "Repairing CR-only comments modified the source chart.");
    }

    private static void VerifyUnrelatedErrorsAreRejected(string directory)
    {
        string[] invalid =
        [
            // Bare numbers next to each other could be a typo of one number.
            """{"angleData": [0 90], "settings": {}, "actions": []}""",
            // A missing colon is not a missing comma.
            """{"angleData": [0], "settings" {}, "actions": []}""",
            // An object member without a key stays invalid after a comma.
            """{"angleData": [0], "settings": {}, "actions": [] {"floor": 1}}""",
            // Unterminated array.
            """{"angleData": [0, "settings": {}}"""
        ];
        for (int i = 0; i < invalid.Length; i++)
        {
            string path = Path.Combine(directory, $"invalid-{i}.adofai");
            File.WriteAllText(path, invalid[i], new UTF8Encoding(false));
            bool rejected = false;
            try
            {
                WpfLevelLoader.Load(path);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // The flat loader can reject the structure before the JSON
                // reader does; either way the chart is not accepted.
                rejected = true;
            }
            Require(rejected, $"Invalid chart {i} was accepted: {invalid[i]}");
        }
    }

    private static void VerifySaveKeepsEveryEvent(string directory)
    {
        string broken = """
            {"angleData": [0, 90, 180], "settings": { "version": 15, "bpm": 120 }, "actions": [
                { "floor": 1, "eventType": "Twirl", "unknownField": 7 }
                { "floor": 2, "eventType": "Flash", "duration": 1, "startColor": "00ff00" }
            ], "decorations": [
                { "floor": 1, "eventType": "AddDecoration", "tag": "d1", "decorationImage": "x.png", "mysteryProperty": [1, 2] }
                { "floor": 2, "eventType": "AddText", "decText": "hi" }
            ]}
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
        string path = Path.Combine(directory, "save-source.adofai");
        string saved = Path.Combine(directory, "save-output.adofai");
        File.WriteAllText(path, broken, new UTF8Encoding(false));

        LevelDocument level = WpfLevelLoader.Load(path).Document;
        var editor = new EditorSession(level);
        editor.SaveAsync(saved).GetAwaiter().GetResult();

        JsonObject root = (JsonObject)JsonNode.Parse(File.ReadAllText(saved))!;
        JsonArray actions = (JsonArray)root["actions"]!;
        JsonArray decorations = (JsonArray)root["decorations"]!;
        Require(actions.Count == 2 && decorations.Count == 2,
            $"Saving a repaired chart lost events: actions {actions.Count}, decorations {decorations.Count}.");
        Require(actions[0]!["unknownField"]?.GetValue<int>() == 7 &&
                decorations[0]!["mysteryProperty"] is JsonArray { Count: 2 } &&
                decorations[1]!["decText"]?.GetValue<string>() == "hi",
            "Saving a repaired chart dropped unknown properties.");
    }

    private static void VerifyPhantigmaIfPresent(string directory)
    {
        string phantigma = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "4hf3bmk", "main.adofai");
        if (!File.Exists(phantigma))
        {
            Console.WriteLine("SKIP: Phantigma main.adofai is not present on this machine.");
            return;
        }

        // Hand-corrected copy: the one missing comma supplied by hand.
        string text = File.ReadAllText(phantigma);
        System.Text.RegularExpressions.Match gap =
            System.Text.RegularExpressions.Regex.Match(text, @"\}(\s*\n\s*)\{");
        Require(gap.Success, "Phantigma fixture no longer contains the known missing comma.");
        int end = gap.Index + 1;
        string corrected = text[..end] + "," + text[end..];
        string correctedPath = Path.Combine(directory, "phantigma-fixed.adofai");
        File.WriteAllText(correctedPath, corrected, new UTF8Encoding(true));

        var watch = Stopwatch.StartNew();
        WpfLevelLoadResult loaded = WpfLevelLoader.Load(phantigma);
        watch.Stop();
        WpfLevelLoadResult reference = WpfLevelLoader.Load(correctedPath);
        Require(loaded.Document.FloorCount == reference.Document.FloorCount &&
                loaded.Document.ActionCount == reference.Document.ActionCount &&
                loaded.Document.DecorationCount == reference.Document.DecorationCount,
            $"Phantigma differs from the hand-corrected copy: floors {loaded.Document.FloorCount}/{reference.Document.FloorCount}, " +
            $"actions {loaded.Document.ActionCount}/{reference.Document.ActionCount}, " +
            $"decorations {loaded.Document.DecorationCount}/{reference.Document.DecorationCount}.");
        Require(loaded.JsonRepairs.Count == 1, $"Phantigma reported {loaded.JsonRepairs.Count} repairs (expected 1).");
        Console.WriteLine(
            $"Phantigma loaded with 1 repair at line {loaded.JsonRepairs[0].Line} column {loaded.JsonRepairs[0].Column}: " +
            $"floors {loaded.Document.FloorCount:N0}, actions {loaded.Document.ActionCount:N0}, " +
            $"decorations {loaded.Document.DecorationCount:N0}, {watch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
