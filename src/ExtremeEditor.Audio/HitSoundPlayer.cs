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

public sealed class HitSoundPlayer : IDisposable
{
    private const int MixerSampleRate = 44_100;
    private const int MixerChannels = 2;

    private readonly WaveFormat _mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixerSampleRate, MixerChannels);
    private readonly MixingSampleProvider _mixer;
    private readonly Dictionary<string, CachedSound> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private WaveOutEvent? _output;

    public HitSoundPlayer()
    {
        _mixer = new MixingSampleProvider(_mixerFormat) { ReadFully = true };
        ReloadAssets();
    }

    public int LoadedCount => _sounds.Count;
    public string AssetSummary => _sounds.Count == 0 ? "hitsounds none" : $"hitsounds {_sounds.Count}";

    public void ReloadAssets()
    {
        _sounds.Clear();
        _offsets.Clear();

        string directory = HitSoundAssetCache.CacheDirectory;
        if (!Directory.Exists(directory))
            return;

        foreach (string path in Directory.GetFiles(directory, "*.wav", SearchOption.TopDirectoryOnly))
        {
            try
            {
                CachedSound sound = LoadCachedSound(path);
                _sounds[NormalizeName(Path.GetFileNameWithoutExtension(path))] = sound;
            }
            catch
            {
                // One malformed/missing game asset must not disable all other hit sounds.
            }
        }

        LoadManifest(Path.Combine(directory, "hitsounds.tsv"));
    }

    public bool Play(string hitSound, double volume)
    {
        if (!_sounds.TryGetValue(NormalizeName(hitSound), out CachedSound? sound))
            return false;

        EnsureOutput();
        var source = new CachedSoundSampleProvider(sound);
        var volumeProvider = new VolumeSampleProvider(source)
        {
            Volume = (float)Math.Clamp(volume, 0.0, 1.0)
        };
        _mixer.AddMixerInput(volumeProvider);
        return true;
    }

    public double GetOffsetSeconds(string hitSound) =>
        _offsets.TryGetValue(NormalizeName(hitSound), out double offset) ? offset : 0.0;

    public void Dispose()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        GC.SuppressFinalize(this);
    }

    private void EnsureOutput()
    {
        if (_output is not null)
            return;

        _output = new WaveOutEvent { DesiredLatency = 50 };
        _output.Init(_mixer);
        _output.Play();
    }

    private CachedSound LoadCachedSound(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;

        if (provider.WaveFormat.Channels == 1)
            provider = new MonoToStereoSampleProvider(provider);
        else if (provider.WaveFormat.Channels != MixerChannels)
            throw new InvalidDataException($"Unsupported hit-sound channel count: {provider.WaveFormat.Channels}");

        if (provider.WaveFormat.SampleRate != MixerSampleRate)
            provider = new WdlResamplingSampleProvider(provider, MixerSampleRate);

        var samples = new List<float>(Math.Max(1024, reader.WaveFormat.SampleRate));
        var buffer = new float[MixerSampleRate * MixerChannels / 4];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                samples.Add(buffer[i]);
        }

        return new CachedSound(samples.ToArray(), _mixerFormat);
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

            if (double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double offset))
                _offsets[NormalizeName(fields[0])] = offset;
        }
    }

    private static string NormalizeName(string name)
    {
        string normalized = name.Trim();
        if (normalized.StartsWith("snd", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[3..];
        return normalized;
    }

    private sealed record CachedSound(float[] Samples, WaveFormat WaveFormat);

    private sealed class CachedSoundSampleProvider : ISampleProvider
    {
        private readonly CachedSound _sound;
        private int _position;

        public CachedSoundSampleProvider(CachedSound sound) => _sound = sound;
        public WaveFormat WaveFormat => _sound.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int available = _sound.Samples.Length - _position;
            int toCopy = Math.Min(available, count);
            if (toCopy <= 0)
                return 0;

            Array.Copy(_sound.Samples, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }
}
