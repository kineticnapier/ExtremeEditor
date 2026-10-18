using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtremeEditor.Core;

/// <summary>
/// Local-only authoring packages for a planned audio-time collaboration.
/// This deliberately creates fresh, independent blank charts, not lossless
/// slices of an existing ADOFAI chart or a merged final chart.
/// </summary>
public sealed record TogetherPackageExportResult(
    string Directory,
    int PackageCount,
    string AudioSha256);

public static class TogetherPackageExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static TogetherPackageExportResult ExportBlankPackages(
        TogetherSectionPlan plan,
        string audioFile,
        string destinationDirectory,
        double initialBpm)
    {
        TogetherSectionPlanner.Validate(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!double.IsFinite(initialBpm) || initialBpm <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialBpm),
                "The template BPM must be a positive finite number.");

        string source = Path.GetFullPath(audioFile);
        string output = Path.GetFullPath(destinationDirectory);
        if (!File.Exists(source))
            throw new FileNotFoundException("The measured source audio must exist.", source);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("An output with that name already exists. Choose a new folder.");
        if (plan.Sections.Count == 0)
            throw new InvalidOperationException("At least one planned section is required.");

        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".wav" or ".ogg" or ".mp3"))
            throw new NotSupportedException("Package audio must be WAV, OGG or MP3.");

        RejectLinkedAncestors(source);
        RejectLinkedAncestors(output);

        string parent = Path.GetDirectoryName(output)
            ?? throw new ArgumentException("Output needs a parent directory.", nameof(destinationDirectory));
        Directory.CreateDirectory(parent);
        string temp = Path.Combine(parent,
            ".extremeeditor-parts-" + Guid.NewGuid().ToString("N"));

        using FileStream stream = File.OpenRead(source);
        string audioHash = Convert.ToHexString(SHA256.HashData(stream));
        var manifestParts = new JsonArray();
        Directory.CreateDirectory(temp);
        try
        {
            for (int i = 0; i < plan.Sections.Count; i++)
            {
                TogetherSection part = plan.Sections[i];
                string folderName = "Part-" + (i + 1).ToString("D3",
                    System.Globalization.CultureInfo.InvariantCulture);
                string partPath = Path.Combine(temp, folderName);
                Directory.CreateDirectory(partPath);
                string songName = Path.GetFileName(source);
                File.Copy(source, Path.Combine(partPath, songName));
                string chartFile = folderName + ".adofai";
                string chartPath = Path.Combine(partPath, chartFile);

                // Floor zero is the local editing origin on the original full
                // audio timeline. One ordinary angle gives the stock editor a
                // minimal editable path; it is NOT an authored musical note.
                double startMs = part.StartSample * 1000.0 / plan.SampleRate;
                JsonObject chart = new()
                {
                    ["angleData"] = new JsonArray(JsonValue.Create(0)),
                    ["settings"] = new JsonObject
                    {
                        ["bpm"] = initialBpm,
                        ["songFilename"] = songName,
                        ["offset"] = startMs,
                        ["pitch"] = 100,
                        ["countdownTicks"] = 0,
                        ["separateCountdownTime"] = false,
                        ["volume"] = 100,
                        ["hitsound"] = "Kick",
                        ["hitsoundVolume"] = 100
                    },
                    ["actions"] = new JsonArray(),
                    ["decorations"] = new JsonArray()
                };
                using (FileStream chartStream = new(chartPath,
                    FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(chartStream, chart, JsonOptions);
                    chartStream.Flush(flushToDisk: true);
                }

                // Parser readback plus the modeled playback clock protects
                // against a wrong audio origin in generated package metadata.
                LevelDocument reopened = AdoFaiLoader.Load(chartPath).Document;
                TimingMap timing = TimingMapBuilder.Build(reopened);
                double actualStart = PlaybackClock.ChartToAudioTime(
                    reopened, timing.GetEntryTime(0));
                if (reopened.FloorCount != 2 ||
                    reopened.ActionStore.ActionCount != 0 ||
                    reopened.SongFilename != songName ||
                    Math.Abs(actualStart - (double)part.StartSample / plan.SampleRate) >
                        0.5 / plan.SampleRate + 1e-9)
                    throw new InvalidDataException(
                        "Generated chart failed loader/audio-origin readback.");

                manifestParts.Add(new JsonObject
                {
                    ["segmentId"] = part.InternalId,
                    ["ownerId"] = part.OwnerName,
                    ["folder"] = folderName,
                    ["chartFile"] = chartFile,
                    ["startSample"] = part.StartSample,
                    ["endSampleExclusive"] = part.EndSampleExclusive
                });
            }

            JsonObject manifest = new()
            {
                ["format"] = "extremeeditor-blank-part-packages-v1",
                ["audioFile"] = Path.GetFileName(source),
                ["audioSha256"] = audioHash,
                ["sampleRate"] = plan.SampleRate,
                ["totalSamples"] = plan.TotalSamples,
                ["initialBpm"] = initialBpm,
                ["description"] = "Independent blank charts with full-length audio. Time ranges are assignment guidance only; native ADOFAI does not enforce them. This is not a lossless chart splitter.",
                ["parts"] = manifestParts
            };
            using (FileStream manifestStream = new(
                Path.Combine(temp, "together-manifest.json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);
                manifestStream.Flush(flushToDisk: true);
            }

            RejectLinkedAncestors(output);
            if (Directory.Exists(output) || File.Exists(output))
                throw new IOException("The output appeared during generation; nothing was overwritten.");
            Directory.Move(temp, output);
            return new TogetherPackageExportResult(output, plan.Sections.Count, audioHash);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RejectLinkedAncestors(string path)
    {
        for (string? current = path; current is not null;
             current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !System.IO.Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Collaboration packages cannot use linked paths.");
        }
    }
}
