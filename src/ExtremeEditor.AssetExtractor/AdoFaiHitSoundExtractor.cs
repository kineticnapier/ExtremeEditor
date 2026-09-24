using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Fmod5Sharp;
using NVorbis;

namespace ExtremeEditor.AssetExtractor;

public sealed record HitSoundExtractionResult(
    string OutputDirectory,
    string KickPath,
    int HitSoundCount);

public static class AdoFaiHitSoundExtractor
{
    public static HitSoundExtractionResult Extract(string gameRoot, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string root = Path.GetFullPath(gameRoot.Trim('"'));
        string dataDirectory = Path.Combine(root, "A Dance of Fire and Ice_Data");
        string resourcesPath = Path.Combine(dataDirectory, "resources.assets");
        string assemblyPath = Path.Combine(dataDirectory, "Managed", "Assembly-CSharp.dll");
        if (!File.Exists(resourcesPath))
            throw new DirectoryNotFoundException($"ADOFAI resources.assets was not found: {resourcesPath}");
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("ADOFAI Assembly-CSharp.dll was not found.", assemblyPath);

        string output = Path.GetFullPath(outputDirectory.Trim('"'));
        Directory.CreateDirectory(output);

        IReadOnlyList<string> hitSoundNames = ReadHitSoundEnumNames(assemblyPath);
        string classDataPath = ClassDataCache.Resolve();
        Console.WriteLine($"[extract] classdata={classDataPath}");
        Console.WriteLine($"[extract] HitSound enum values={hitSoundNames.Count}");

        var manager = new AssetsManager();
        try
        {
            manager.LoadClassPackage(classDataPath);
            AssetsFileInstance instance = manager.LoadAssetsFile(resourcesPath, false);
            manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);

            Dictionary<string, AssetFileInfo> clipsByName = FindNamedAssets(
                manager,
                instance,
                AssetClassID.AudioClip);

            int exported = 0;
            int missing = 0;
            var failures = new List<string>();

            foreach (string hitSoundName in hitSoundNames)
            {
                if (string.Equals(hitSoundName, "None", StringComparison.OrdinalIgnoreCase))
                    continue;

                string clipName = "snd" + hitSoundName;
                if (!clipsByName.TryGetValue(clipName, out AssetFileInfo? clipInfo))
                {
                    Console.WriteLine($"[extract] {hitSoundName}: missing AudioClip {clipName}");
                    missing++;
                    continue;
                }

                try
                {
                    ExtractClip(manager, instance, clipInfo, dataDirectory, output, clipName);
                    exported++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{hitSoundName}: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"[extract] {hitSoundName}: FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }

            string kickPath = Path.Combine(output, "sndKick.wav");
            if (!File.Exists(kickPath))
                throw new InvalidDataException("HitSound extraction did not produce sndKick.wav.");
            if (exported == 0)
                throw new InvalidDataException("No HitSound-enum AudioClips were extracted.");

            Console.WriteLine(
                $"[extract] hitsounds exported={exported} missing={missing} failed={failures.Count}");
            if (failures.Count > 0)
                Console.WriteLine($"[extract] hitsound failures: {string.Join(" | ", failures)}");

            return new HitSoundExtractionResult(output, kickPath, exported);
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static IReadOnlyList<string> ReadHitSoundEnumNames(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            throw new InvalidDataException("Assembly-CSharp.dll does not contain CLR metadata.");

        MetadataReader metadata = peReader.GetMetadataReader();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            if (!string.Equals(metadata.GetString(type.Name), "HitSound", StringComparison.Ordinal))
                continue;
            if (!IsEnumType(metadata, type))
                continue;

            var names = new List<string>();
            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Literal) == 0)
                    continue;

                string name = metadata.GetString(field.Name);
                if (!string.Equals(name, "value__", StringComparison.Ordinal))
                    names.Add(name);
            }

            if (names.Count == 0)
                throw new InvalidDataException("HitSound enum was found but contained no literals.");

            return names;
        }

        throw new InvalidDataException("HitSound enum was not found in Assembly-CSharp.dll.");
    }

    private static bool IsEnumType(MetadataReader metadata, TypeDefinition type)
    {
        EntityHandle baseType = type.BaseType;
        if (baseType.Kind == HandleKind.TypeReference)
        {
            TypeReference reference = metadata.GetTypeReference((TypeReferenceHandle)baseType);
            return string.Equals(metadata.GetString(reference.Namespace), "System", StringComparison.Ordinal) &&
                   string.Equals(metadata.GetString(reference.Name), "Enum", StringComparison.Ordinal);
        }

        return false;
    }

    private static Dictionary<string, AssetFileInfo> FindNamedAssets(
        AssetsManager manager,
        AssetsFileInstance instance,
        AssetClassID classId)
    {
        var result = new Dictionary<string, AssetFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (AssetFileInfo info in instance.file.GetAssetsOfType(classId))
        {
            AssetTypeValueField root = manager.GetBaseField(instance, info);
            AssetTypeValueField nameField = root["m_Name"];
            if (nameField.IsDummy || string.IsNullOrWhiteSpace(nameField.AsString))
                continue;

            result.TryAdd(nameField.AsString, info);
        }

        return result;
    }

    private static void ExtractClip(
        AssetsManager manager,
        AssetsFileInstance instance,
        AssetFileInfo clipInfo,
        string dataDirectory,
        string outputDirectory,
        string clipName)
    {
        AssetTypeValueField clip = manager.GetBaseField(instance, clipInfo);
        AssetTypeValueField resource = clip["m_Resource"];
        if (resource.IsDummy)
            throw new InvalidDataException($"{clipName} m_Resource could not be decoded.");

        string source = resource["m_Source"].AsString;
        ulong offset = ReadUnsigned(resource["m_Offset"], "m_Offset", clipName);
        ulong size = ReadUnsigned(resource["m_Size"], "m_Size", clipName);
        if (size == 0)
            throw new InvalidDataException($"{clipName} resource size is zero.");
        if (size > int.MaxValue)
            throw new InvalidDataException($"{clipName} resource is unexpectedly large: {size} bytes.");

        string resourcePath = ResolveResourcePath(dataDirectory, source, clipName);
        byte[] fsb = ReadResourceRange(resourcePath, offset, checked((int)size), clipName);
        if (fsb.Length < 4 || fsb[0] != (byte)'F' || fsb[1] != (byte)'S' || fsb[2] != (byte)'B' || fsb[3] != (byte)'5')
        {
            throw new InvalidDataException(
                $"{clipName} resource payload is not FSB5: {Path.GetFileName(resourcePath)} offset={offset} size={size}.");
        }

        var bank = FsbLoader.LoadFsbFromByteArray(fsb);
        if (bank.Samples.Count == 0)
            throw new InvalidDataException($"{clipName} FSB5 contains no samples.");

        var sample = bank.Samples[0];
        if (!sample.RebuildAsStandardFileFormat(out byte[]? rebuilt, out string? extension) ||
            rebuilt is null || rebuilt.Length == 0 || string.IsNullOrWhiteSpace(extension))
        {
            throw new NotSupportedException(
                $"{clipName} FSB5 codec {bank.Header.AudioType} could not be rebuilt by Fmod5Sharp.");
        }

        string destination = Path.Combine(outputDirectory, clipName + ".wav");
        if (string.Equals(extension, "wav", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(destination, rebuilt);
        }
        else if (string.Equals(extension, "ogg", StringComparison.OrdinalIgnoreCase))
        {
            ConvertVorbisToPcm16Wave(rebuilt, destination);
        }
        else
        {
            throw new NotSupportedException(
                $"{clipName} FSB5 codec {bank.Header.AudioType} rebuilt as unsupported format '{extension}'.");
        }

        string compression = clip["m_CompressionFormat"].IsDummy
            ? "<unknown>"
            : clip["m_CompressionFormat"].AsInt.ToString();
        Console.WriteLine(
            $"[extract] {clipName} pathId={clipInfo.PathId} resource={Path.GetFileName(resourcePath)} offset={offset} size={size} compression={compression} fsbType={bank.Header.AudioType} channels={sample.Metadata.Channels} frequency={sample.Metadata.Frequency} -> {Path.GetFileName(destination)}");
    }

    private static string ResolveResourcePath(string dataDirectory, string source, string clipName)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidDataException($"{clipName} m_Resource.m_Source is empty.");

        string normalized = source.Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(dataDirectory, normalized));
        if (File.Exists(candidate))
            return candidate;

        string fileNameCandidate = Path.Combine(dataDirectory, Path.GetFileName(normalized));
        if (File.Exists(fileNameCandidate))
            return fileNameCandidate;

        throw new FileNotFoundException($"{clipName} resource file was not found.", candidate);
    }

    private static byte[] ReadResourceRange(string path, ulong offset, int size, string clipName)
    {
        using FileStream stream = File.OpenRead(path);
        if (offset > (ulong)stream.Length || offset + (ulong)size > (ulong)stream.Length)
        {
            throw new InvalidDataException(
                $"{clipName} resource range is outside {Path.GetFileName(path)}: offset={offset} size={size} length={stream.Length}.");
        }

        stream.Position = checked((long)offset);
        byte[] data = new byte[size];
        stream.ReadExactly(data);
        return data;
    }

    private static ulong ReadUnsigned(AssetTypeValueField field, string fieldName, string clipName)
    {
        if (field.IsDummy)
            throw new InvalidDataException($"{clipName} {fieldName} could not be decoded.");

        return field.TemplateField.ValueType switch
        {
            AssetValueType.UInt64 => field.AsULong,
            AssetValueType.Int64 => checked((ulong)field.AsLong),
            AssetValueType.UInt32 => field.AsUInt,
            AssetValueType.Int32 => checked((ulong)field.AsInt),
            _ => throw new InvalidDataException(
                $"{clipName} {fieldName} has unsupported serialized type {field.TemplateField.ValueType}.")
        };
    }

    private static void ConvertVorbisToPcm16Wave(byte[] ogg, string destination)
    {
        using var source = new MemoryStream(ogg, writable: false);
        using var reader = new VorbisReader(source, closeOnDispose: false);
        int channels = reader.Channels;
        int sampleRate = reader.SampleRate;
        if (channels <= 0 || sampleRate <= 0)
        {
            throw new InvalidDataException(
                $"Decoded Vorbis metadata is invalid: channels={channels}, sampleRate={sampleRate}.");
        }

        using var pcm = new MemoryStream();
        using (var writer = new BinaryWriter(pcm, Encoding.ASCII, leaveOpen: true))
        {
            float[] samples = new float[Math.Max(4096, channels * 4096)];
            while (true)
            {
                int read = reader.ReadSamples(samples, 0, samples.Length);
                if (read <= 0)
                    break;

                for (int i = 0; i < read; i++)
                {
                    float value = Math.Clamp(samples[i], -1f, 1f);
                    short pcm16 = value <= -1f
                        ? short.MinValue
                        : value >= 1f
                            ? short.MaxValue
                            : (short)MathF.Round(value * short.MaxValue);
                    writer.Write(pcm16);
                }
            }
            writer.Flush();
        }

        WritePcm16Wave(destination, channels, sampleRate, pcm.ToArray());
    }

    private static void WritePcm16Wave(string path, int channels, int sampleRate, byte[] pcm)
    {
        const short bitsPerSample = 16;
        short blockAlign = checked((short)(channels * (bitsPerSample / 8)));
        int byteRate = checked(sampleRate * blockAlign);
        int riffSize = checked(36 + pcm.Length);

        using FileStream output = File.Create(path);
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write(checked((short)channels));
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }
}
