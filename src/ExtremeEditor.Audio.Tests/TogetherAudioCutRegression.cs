using System.Buffers.Binary;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;
using NAudio.Wave;

internal static class TogetherAudioCutRegression
{
    private const int Rate = 8_000;
    private const int Channels = 2;
    private const int Frames = 400;

    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "extremeeditor-audio-cut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "song.wav");
            using (var writer = new WaveFileWriter(source, new WaveFormat(Rate, 16, Channels)))
            {
                byte[] sample = new byte[4];
                for (int frame = 0; frame < Frames; frame++)
                {
                    for (int channel = 0; channel < Channels; channel++)
                        BinaryPrimitives.WriteInt16LittleEndian(
                            sample.AsSpan(channel * 2, 2), Value(frame, channel));
                    writer.Write(sample, 0, sample.Length);
                }
            }

            TogetherSectionPlan plan = TogetherSectionPlanner.CreateWholeSong(Rate, Frames);
            plan = TogetherSectionPlanner.Split(plan, "section-1", 79);
            plan = TogetherSectionPlanner.Split(plan, plan.Sections[1].InternalId, 211);
            string destination = Path.Combine(root, "parts");
            TogetherPackageExportResult result =
                TogetherPackageExporter.ExportBlankPackages(
                    plan, source, destination, 120, new TogetherAudioWaveClipWriter());
            Check(result.PackageCount == 3, "three packages");

            int[] boundaries = [0, 79, 211, Frames];
            for (int section = 0; section < 3; section++)
            {
                string name = $"Part-{section + 1:D3}";
                string directory = Path.Combine(destination, name);
                using var reader = new WaveFileReader(Path.Combine(directory, name + ".wav"));
                int length = boundaries[section + 1] - boundaries[section];
                Check(reader.WaveFormat.SampleRate == Rate &&
                      reader.WaveFormat.Channels == Channels &&
                      reader.WaveFormat.BitsPerSample == 16 &&
                      reader.Length / reader.WaveFormat.BlockAlign == length,
                    "exact sliced WAV length and format");
                byte[] samples = new byte[checked((int)reader.Length)];
                int read = 0;
                while (read < samples.Length)
                {
                    int count = reader.Read(samples, read, samples.Length - read);
                    Check(count > 0, "incomplete WAV read");
                    read += count;
                }
                for (int frame = 0; frame < length; frame++)
                    for (int channel = 0; channel < Channels; channel++)
                    {
                        short actual = BinaryPrimitives.ReadInt16LittleEndian(
                            samples.AsSpan((frame * Channels + channel) * 2, 2));
                        Check(Math.Abs(actual - Value(boundaries[section] + frame, channel)) <= 1,
                            "decoded sample mismatch at section " + section + " frame " + frame);
                    }

                var chart = AdoFaiLoader.Load(
                    Path.Combine(directory, name + ".adofai")).Document;
                Check(chart.SongFilename == name + ".wav" &&
                    Math.Abs(PlaybackClock.ChartToAudioTime(
                        chart, TimingMapBuilder.Build(chart).GetEntryTime(0))) < 1e-9,
                    "chart starts at local cut zero");
                Check(File.Exists(Path.Combine(destination, name + ".zip")),
                    "part archive");
            }

            bool duplicateRejected = false;
            try
            {
                TogetherPackageExporter.ExportBlankPackages(
                    plan, source, destination, 120, new TogetherAudioWaveClipWriter());
            }
            catch (IOException) { duplicateRejected = true; }
            Check(duplicateRejected, "export never overwrites existing packages");

            Console.WriteLine("PASS: collaboration WAV sample-exact cut and local chart origin.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static short Value(int frame, int channel) =>
        (short)((((frame * 37 + channel * 17) % 256) - 128) * 128);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Audio cut regression: " + message);
    }
}
