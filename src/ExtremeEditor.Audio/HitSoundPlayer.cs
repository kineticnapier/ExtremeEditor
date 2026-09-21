using System.Globalization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ExtremeEditor.Audio;

public sealed record HitSoundImportResult(int ImportedClips, string CacheDirectory, bool HasManifest);

public static class HitSoundAssetCache
{
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "AssetCache", "hitsounds");

    public static HitSoundImportResult ImportProbeFolder(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException(sourceDirectory);

        string[] wavFiles = Directory.GetFiles(sourceDirectory, "*.wav", SearchOption.TopDirectoryOnly);
        if (wavFiles.Length == 0)
            throw new InvalidDataException("No WAV files were found in the selected hit-sound probe folder.");

        Directory.CreateDirectory(CacheDirectory);
        foreach (string old in Directory.GetFiles(CacheDirectory, "*.wav", SearchOption.TopDirectoryOnly))
            File.Delete(old);

        int imported = 0;
        foreach (string source in wavFiles)
        {
            string destination = Path.Combine(CacheDirectory, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: true);
            imported++;
        }

        string sourceManifest = Path.Combine(sourceDirectory, "hitsounds.tsv");
        string destinationManifest = Path.Combine(CacheDirectory, "hitsounds.tsv");
        bool hasManifest = File.Exists(sourceManifest);
        if (hasManifest)
            File.Copy(sourceManifest, destinationManifest, overwrite: true);
        else if (File.Exists(destinationManifest))
            File.Delete(destinationManifest);

        return new HitSoundImportResult(imported, CacheDirectory, hasManifest);
    }
}

/// <summary>
/// Owns the small, reusable hit-sound PCM cache. It deliberately owns no output
/// device: song and hit sounds are rendered by AudioPlayer's single graph.
/// </summary>
internal sealed class HitSoundLibrary
{
    private const int CacheSampleRate = 44_100;
    private const int Channels = 2;

    private readonly Dictionary<string, CachedHitSound> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, IReadOnlyDictionary<string, RenderedHitSound>> _renderedBySampleRate = new();

    public int LoadedCount => _sounds.Count;
    public string AssetSummary => _sounds.Count == 0 ? "hitsounds none" : $"hitsounds {_sounds.Count}";

    public void ReloadAssets()
    {
        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;
        using IDisposable? measurement = log?.Measure("hitsounds.reload");
        _sounds.Clear();
        _offsets.Clear();
        _renderedBySampleRate.Clear();

        string directory = HitSoundAssetCache.CacheDirectory;
        if (!Directory.Exists(directory))
        {
            log?.Write("hitsounds.directory_missing", $"path={directory}");
            return;
        }

        string[] paths = Directory.GetFiles(directory, "*.wav", SearchOption.TopDirectoryOnly);
        log?.Write("hitsounds.enumerated", $"path={directory} count={paths.Length}");
        foreach (string path in paths)
        {
            try
            {
                using IDisposable? fileMeasurement = log?.Measure(
                    "hitsounds.load_file", $"file={Path.GetFileName(path)} bytes={new FileInfo(path).Length}");
                CachedHitSound sound = LoadCachedSound(path, log);
                _sounds[NormalizeName(Path.GetFileNameWithoutExtension(path))] = sound;
                log?.Write("hitsounds.loaded_file",
                    $"file={Path.GetFileName(path)} samples={sound.Samples.Length} " +
                    $"rate={sound.WaveFormat.SampleRate} channels={sound.WaveFormat.Channels}");
            }
            catch (Exception ex)
            {
                log?.Write("hitsounds.load_failed",
                    $"file={Path.GetFileName(path)} exception={ex.GetType().FullName} message={ex.Message}");
                // One malformed/missing game asset must not disable all other hit sounds.
            }
        }

        LoadManifest(Path.Combine(directory, "hitsounds.tsv"));
        log?.Write("hitsounds.reload_complete", $"loaded={_sounds.Count} offsets={_offsets.Count}");
    }

    public IReadOnlyDictionary<string, RenderedHitSound> RenderFor(int sampleRate)
    {
        if (_renderedBySampleRate.TryGetValue(sampleRate, out IReadOnlyDictionary<string, RenderedHitSound>? cached))
            return cached;

        AudioDiagnosticLog? log = AudioDiagnosticLog.Shared;
        using IDisposable? measurement = log?.Measure(
            "hitsounds.render_all", $"target_rate={sampleRate} clip_count={_sounds.Count}");
        var rendered = new Dictionary<string, RenderedHitSound>(_sounds.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, CachedHitSound sound) in _sounds)
        {
            using IDisposable? clipMeasurement = log?.Measure(
                "hitsounds.render_clip",
                $"name={name} input_samples={sound.Samples.Length} input_rate={sound.WaveFormat.SampleRate} " +
                $"target_rate={sampleRate}");
            ISampleProvider provider = new CachedSoundSampleProvider(sound.Samples, sound.WaveFormat);
            if (sampleRate != CacheSampleRate)
                provider = new WdlResamplingSampleProvider(provider, sampleRate);

            int capacity = Math.Max(1024, sound.Samples.Length * sampleRate / CacheSampleRate);
            float[] samples = ReadAll(provider, capacity, $"render:{name}", log);
            double offset = _offsets.TryGetValue(name, out double value) ? value : 0.0;
            rendered[name] = new RenderedHitSound(samples, offset);
            log?.Write("hitsounds.rendered_clip", $"name={name} output_samples={samples.Length}");
        }

        _renderedBySampleRate[sampleRate] = rendered;
        return rendered;
    }

    private static CachedHitSound LoadCachedSound(string path, AudioDiagnosticLog? log)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        log?.Write("hitsounds.source_format",
            $"file={Path.GetFileName(path)} provider={provider.GetType().FullName} " +
            $"rate={provider.WaveFormat.SampleRate} channels={provider.WaveFormat.Channels} " +
            $"duration_ms={reader.TotalTime.TotalMilliseconds:F3}");

        if (provider.WaveFormat.Channels == 1)
            provider = new MonoToStereoSampleProvider(provider);
        else if (provider.WaveFormat.Channels != Channels)
            throw new InvalidDataException($"Unsupported hit-sound channel count: {provider.WaveFormat.Channels}");

        if (provider.WaveFormat.SampleRate != CacheSampleRate)
            provider = new WdlResamplingSampleProvider(provider, CacheSampleRate);

        return new CachedHitSound(ReadAll(provider, Math.Max(1024, reader.WaveFormat.SampleRate),
                $"cache:{Path.GetFileName(path)}", log),
            WaveFormat.CreateIeeeFloatWaveFormat(CacheSampleRate, Channels));
    }

    private static float[] ReadAll(
        ISampleProvider provider,
        int initialCapacity,
        string operation,
        AudioDiagnosticLog? log)
    {
        using IDisposable? measurement = log?.Measure(
            "hitsounds.read_all",
            $"operation={operation} provider={provider.GetType().FullName} " +
            $"rate={provider.WaveFormat.SampleRate} channels={provider.WaveFormat.Channels} " +
            $"initial_capacity={initialCapacity}");
        var samples = new List<float>(initialCapacity);
        var buffer = new float[CacheSampleRate * Channels / 4];
        int iterations = 0;
        long cumulativeSamples = 0;
        using IDisposable? heartbeat = log?.StartHeartbeat(
            "hitsounds.read_heartbeat",
            TimeSpan.FromMilliseconds(250),
            () => $"operation={operation} iterations={Volatile.Read(ref iterations)} " +
                  $"cumulative={Interlocked.Read(ref cumulativeSamples)}");
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            Interlocked.Increment(ref iterations);
            Interlocked.Add(ref cumulativeSamples, read);
            log?.Write("hitsounds.read",
                $"operation={operation} iteration={iterations} read={read} cumulative={cumulativeSamples}");
            for (int i = 0; i < read; i++)
                samples.Add(buffer[i]);
        }

        log?.Write("hitsounds.read_complete",
            $"operation={operation} iterations={iterations} final_read={read} cumulative={cumulativeSamples}");
        return samples.ToArray();
    }

    private void LoadManifest(string path)
    {
        if (!File.Exists(path))
            return;

        foreach (string line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] fields = line.Split('\t');
            if (fields.Length < 3)
                continue;

            if (double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double offset) &&
                double.IsFinite(offset))
                _offsets[NormalizeName(fields[0])] = offset;
        }
    }

    internal static string NormalizeName(string name)
    {
        string normalized = name.Trim();
        if (normalized.StartsWith("snd", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[3..];
        return normalized;
    }

    private sealed record CachedHitSound(float[] Samples, WaveFormat WaveFormat);

    private sealed class CachedSoundSampleProvider(float[] samples, WaveFormat waveFormat) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat => waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int toCopy = Math.Min(samples.Length - _position, count);
            if (toCopy <= 0)
                return 0;

            Array.Copy(samples, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }
}

internal sealed record RenderedHitSound(float[] Samples, double OffsetSeconds)
{
    public int FrameCount => Samples.Length / 2;
}
