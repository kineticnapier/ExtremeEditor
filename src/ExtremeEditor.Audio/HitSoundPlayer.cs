using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ExtremeEditor.Audio;

public static class HitSoundAssetCache
{
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "AssetCache", "hitsounds");
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
    private readonly object _gate = new();

    public int LoadedCount { get { lock (_gate) return _sounds.Count; } }
    public string AssetSummary { get { lock (_gate) return _sounds.Count == 0 ? "hitsounds none" : $"hitsounds {_sounds.Count}"; } }

    public void ReloadAssets()
    {
        lock (_gate)
            ReloadAssetsCore();
    }

    private void ReloadAssetsCore()
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

        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, "*.wav", SearchOption.TopDirectoryOnly);
        }
        catch (UnauthorizedAccessException ex)
        {
            log?.Write("hitsounds.directory_unavailable", $"path={directory} message={ex.Message}");
            return;
        }
        catch (IOException ex)
        {
            log?.Write("hitsounds.directory_unavailable", $"path={directory} message={ex.Message}");
            return;
        }
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
        lock (_gate)
            return RenderForCore(sampleRate);
    }

    private IReadOnlyDictionary<string, RenderedHitSound> RenderForCore(int sampleRate)
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

            int capacity = EstimateRenderCapacity(sound.Samples.Length, sampleRate);
            float[] samples = ReadAll(provider, capacity, $"render:{name}", log);
            double offset = _offsets.TryGetValue(name, out double value) ? value : 0.0;
            rendered[name] = new RenderedHitSound(samples, offset);
            log?.Write("hitsounds.rendered_clip", $"name={name} output_samples={samples.Length}");
        }

        _renderedBySampleRate[sampleRate] = rendered;
        return rendered;
    }

    // The sample-count/rate product overflows int for even ordinary one-second
    // stereo assets. Estimate in long and cap speculative List allocation so
    // a malformed or very long asset cannot reserve enormous RAM up front.
    internal static int EstimateRenderCapacity(int sourceSampleCount, int sampleRate)
    {
        long estimated = (long)sourceSampleCount * sampleRate / CacheSampleRate;
        return (int)Math.Clamp(estimated, 1024L, 4_194_304L);
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

        return new CachedHitSound(ReadAll(provider, EstimateCacheCapacity(reader.TotalTime),
                $"cache:{Path.GetFileName(path)}", log),
            WaveFormat.CreateIeeeFloatWaveFormat(CacheSampleRate, Channels));
    }

    // Reserve for the decoded stereo PCM duration, not just one second of
    // the source sample rate. Bound the estimate to avoid huge speculative
    // allocations for long or malformed audio files.
    internal static int EstimateCacheCapacity(TimeSpan duration)
    {
        double samples = Math.Ceiling(duration.TotalSeconds * CacheSampleRate * Channels);
        return (int)Math.Clamp(samples, 1024.0, 4_194_304.0);
    }

    internal static float[] ReadAll(
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
            // Append the decoded block in one span copy instead of calling
            // List.Add for every sample. SetCount grows the backing array as
            // needed; the newly exposed range is filled immediately.
            int previousCount = samples.Count;
            CollectionsMarshal.SetCount(samples, checked(previousCount + read));
            buffer.AsSpan(0, read).CopyTo(CollectionsMarshal.AsSpan(samples)[previousCount..]);
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

internal static class SharedHitSoundLibrary
{
    private static readonly Lazy<HitSoundLibrary> LazyInstance = new(() =>
    {
        var library = new HitSoundLibrary();
        library.ReloadAssets();
        return library;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static HitSoundLibrary Instance => LazyInstance.Value;
}

internal sealed record RenderedHitSound(float[] Samples, double OffsetSeconds)
{
    public int FrameCount => Samples.Length / 2;
}
