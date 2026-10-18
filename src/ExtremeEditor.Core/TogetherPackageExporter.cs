using System.IO.Compression;
using System.Text;
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
        double initialBpm,
        ITogetherAudioClipWriter clipWriter)
    {
        ArgumentNullException.ThrowIfNull(clipWriter);
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
        var clipRequests = new List<TogetherAudioClipRequest>(plan.Sections.Count);
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
                string songName = folderName + ".wav";
                clipRequests.Add(new TogetherAudioClipRequest(
                    Path.Combine(partPath, songName),
                    part.StartSample,
                    part.EndSampleExclusive));
                string chartFile = folderName + ".adofai";
                string chartPath = Path.Combine(partPath, chartFile);

                // The audio itself is clipped to [start,end). Floor zero must
                // now correspond to 0 seconds of the LOCAL clip, not the
                // original absolute song time. The manifest records that time.
                // One ordinary angle is a minimal editable path, not a note.
                double startMs = 0;
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
                    Math.Abs(actualStart) > 0.5 / plan.SampleRate + 1e-9)
                    throw new InvalidDataException(
                        "Generated chart failed loader/audio-origin readback.");

                string startTime = TimeSpan.FromSeconds(
                    (double)part.StartSample / plan.SampleRate).ToString(
                    @"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture);
                string endTime = TimeSpan.FromSeconds(
                    (double)part.EndSampleExclusive / plan.SampleRate).ToString(
                    @"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(Path.Combine(partPath, "README.txt"),
                    "ExtremeEditor 合作担当ファイル" + Environment.NewLine +
                    "担当区間: " + startTime + " ～ " + endTime +
                    " (終点は含まない)" + Environment.NewLine +
                    "担当者ID: " + (part.OwnerName ?? "未割当") + Environment.NewLine +
                    "譜面: " + chartFile + Environment.NewLine +
                    "音源: " + songName + " (担当範囲だけ切断したPCM WAV)" + Environment.NewLine +
                    "譜面の0秒は切断後の音源の0秒に一致します。" + Environment.NewLine +
                    "元曲の担当開始・終了時刻は上記の通りです。区間外を編集しないでください。" + Environment.NewLine +
                    "この譜面は空の制作テンプレートであり、完成済み譜面からの切り出しではありません。" + Environment.NewLine +
                    "先頭の1タイルは編集開始位置の目印です。採譜したノーツではありません。" + Environment.NewLine +
                    "生成されたファイルが本家ADOFAIエディタで開けるか確認してください。" + Environment.NewLine,
                    Encoding.UTF8);
                manifestParts.Add(new JsonObject
                {
                    ["segmentId"] = part.InternalId,
                    ["ownerId"] = part.OwnerName,
                    ["folder"] = folderName,
                    ["chartFile"] = chartFile,
                    ["audioFile"] = songName,
                    ["clipSampleCount"] = part.EndSampleExclusive - part.StartSample,
                    ["startSample"] = part.StartSample,
                    ["endSampleExclusive"] = part.EndSampleExclusive
                });
            }

            // Decode once, then write each cut as lossless PCM WAV. If any
            // decoder/cut fails, the temporary output is removed and no
            // partially valid package gets published.
            clipWriter.WriteClips(source, plan.SampleRate, clipRequests);
            for (int i = 0; i < plan.Sections.Count; i++)
            {
                string folder = "Part-" + (i + 1).ToString("D3",
                    System.Globalization.CultureInfo.InvariantCulture);
                string audioPath = clipRequests[i].DestinationPath;
                if (!File.Exists(audioPath) || new FileInfo(audioPath).Length <= 44)
                    throw new InvalidDataException(
                        "Cut audio was not generated for " + folder + ".");
                ZipFile.CreateFromDirectory(Path.Combine(temp, folder),
                    Path.Combine(temp, folder + ".zip"),
                    CompressionLevel.Fastest, includeBaseDirectory: false);
            }

            JsonObject manifest = new()
            {
                ["format"] = "extremeeditor-blank-part-packages-v2",
                ["audioFile"] = Path.GetFileName(source),
                ["audioSha256"] = audioHash,
                ["sampleRate"] = plan.SampleRate,
                ["totalSamples"] = plan.TotalSamples,
                ["initialBpm"] = initialBpm,
                ["description"] = "Independent blank charts with sample-aligned audio cuts decoded as PCM16 WAV. Chart time zero is local cut time zero, original global boundaries are in parts. Native ADOFAI does not enforce assignments. Not a lossless chart splitter.",
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
