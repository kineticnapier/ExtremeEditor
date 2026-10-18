using System.IO.Compression;
using System.Text.Json;
using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class TogetherPackageExportRegression
{
    private const int Rate = 48_000;

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "extremeeditor-package-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string audio = Path.Combine(root, "song.ogg");
            byte[] bytes = [0x11, 0x22, 0x33, 0x44, 0x55];
            File.WriteAllBytes(audio, bytes);
            TogetherSectionPlan plan = TogetherSectionPlanner.CreateWholeSong(
                Rate, Rate * 10L);
            plan = TogetherSectionPlanner.Split(plan, "section-1", Rate * 4L);
            plan = TogetherSectionPlanner.AssignOwner(plan, "section-1", "alice");
            string output = Path.Combine(root, "packages");
            var writer = new RecordingClipWriter();
            TogetherPackageExportResult result =
                TogetherPackageExporter.ExportBlankPackages(plan, audio, output, 120, writer);
            Check(writer.Clips?.Count == 2 &&
                  writer.SampleRate == Rate &&
                  writer.Clips[0].StartSample == 0 &&
                  writer.Clips[0].EndSampleExclusive == Rate * 4L &&
                  writer.Clips[1].StartSample == Rate * 4L &&
                  writer.Clips[1].EndSampleExclusive == Rate * 10L,
                "sample-exact cut requests preserve section bounds");

            Check(result.PackageCount == 2 && Directory.Exists(output),
                "both packages created");
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(output, "together-manifest.json")));
            JsonElement parts = manifest.RootElement.GetProperty("parts");
            Check(parts.GetArrayLength() == 2, "manifest section count");
            Check(parts[0].GetProperty("startSample").GetInt64() == 0 &&
                  parts[0].GetProperty("endSampleExclusive").GetInt64() == Rate * 4L &&
                  parts[1].GetProperty("startSample").GetInt64() == Rate * 4L &&
                  parts[1].GetProperty("endSampleExclusive").GetInt64() == Rate * 10L,
                "manifest retains half-open time allocation");
            Check(parts[0].GetProperty("ownerId").GetString() == "alice" &&
                  parts[1].GetProperty("ownerId").ValueKind == JsonValueKind.Null,
                "assigned and unassigned owners are preserved");

            for (int index = 0; index < 2; index++)
            {
                string partFolder = Path.Combine(output, "Part-" + (index + 1).ToString("D3"));
                string packagedAudio = Path.Combine(partFolder,
                    "Part-" + (index + 1).ToString("D3") + ".wav");
                string packagedChart = Path.Combine(partFolder,
                    "Part-" + (index + 1).ToString("D3") + ".adofai");
                string archivePath = partFolder + ".zip";
                using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                {
                    Check(archive.Entries.Any(entry => entry.Name.EndsWith(".wav",
                              StringComparison.Ordinal)) &&
                          archive.Entries.Any(entry => entry.Name == "README.txt") &&
                          archive.Entries.Any(entry => entry.Name.EndsWith(".adofai",
                              StringComparison.Ordinal)),
                        "complete shareable per-part ZIP");
                }
                string guidance = File.ReadAllText(Path.Combine(partFolder, "README.txt"));
                Check(guidance.Contains(index == 0 ? "00:00:00.000" : "00:00:04.000",
                        StringComparison.Ordinal) &&
                      guidance.Contains(index == 0 ? "00:00:04.000" : "00:00:10.000",
                        StringComparison.Ordinal),
                    "participant instructions carry actual interval");
                Check(File.Exists(packagedAudio) &&
                      !File.Exists(Path.Combine(partFolder, "song.ogg")),
                    "only clipped WAV is included, original full audio is not copied");
                LevelDocument doc = AdoFaiLoader.Load(packagedChart).Document;
                Check(doc.FloorCount == 2 && doc.ActionStore.ActionCount == 0 &&
                      doc.SongFilename == Path.GetFileName(packagedAudio) &&
                      doc.InitialBpm == 120,
                    "stock-form template readback");
                double audioOrigin = PlaybackClock.ChartToAudioTime(
                    doc, TimingMapBuilder.Build(doc).GetEntryTime(0));
                Check(Math.Abs(audioOrigin) <= 1e-9,
                    "local floor zero aligns to clipped audio origin");
            }

            byte[] previous = File.ReadAllBytes(
                Path.Combine(output, "Part-001", "Part-001.adofai"));
            ExpectRejected(() => TogetherPackageExporter.ExportBlankPackages(
                plan, audio, output, 120, writer), "do not overwrite existing output");
            Check(File.ReadAllBytes(Path.Combine(
                output, "Part-001", "Part-001.adofai")).SequenceEqual(previous),
                "rejected duplicate export preserves package");
            ExpectRejected(() => TogetherPackageExporter.ExportBlankPackages(
                plan, audio, Path.Combine(root, "invalid"), double.NaN, writer),
                "invalid BPM");
            ExpectRejected(() => TogetherPackageExporter.ExportBlankPackages(
                plan, audio, Path.Combine(root, "bad-bpm"), 0, writer),
                "nonpositive BPM");
            Check(!Directory.Exists(Path.Combine(root, "invalid")) &&
                  !Directory.Exists(Path.Combine(root, "bad-bpm")),
                "rejected input never creates output");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingClipWriter : ITogetherAudioClipWriter
    {
        public int SampleRate { get; private set; }
        public IReadOnlyList<TogetherAudioClipRequest>? Clips { get; private set; }

        public void WriteClips(string sourceAudioPath, int sampleRate,
            IReadOnlyList<TogetherAudioClipRequest> clips)
        {
            SampleRate = sampleRate;
            Clips = clips.ToArray();
            foreach (TogetherAudioClipRequest part in clips)
            {
                // Fake clip bytes are only for testing Core's orchestration.
                // Real PCM slicing is implemented in ExtremeEditor.Audio.
                File.WriteAllBytes(part.DestinationPath, new byte[64]);
            }
        }
    }

    private static void ExpectRejected(Action action, string label)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or IOException
            or InvalidOperationException or NotSupportedException) { return; }
        throw new InvalidOperationException("Expected rejection: " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException("Together package regression: " + label);
    }
}
