using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace ExtremeEditor.AssetExtractor;

public sealed record FloorTextureExtractionResult(
    string OutputDirectory,
    string TilePath,
    string PerlinPath,
    string RampPath,
    string GlowPath);

public static class AdoFaiFloorTextureExtractor
{
    private static readonly (string PropertyName, string FileName)[] RequiredTextures =
    [
        ("_TileTex", "tile.png"),
        ("_PerlinTex", "perlin.png"),
        ("_MainTex", "ramp.png")
    ];

    public static FloorTextureExtractionResult Extract(string gameRoot, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string root = Path.GetFullPath(gameRoot.Trim('"'));
        string dataDirectory = Path.Combine(root, "A Dance of Fire and Ice_Data");
        string resourcesPath = Path.Combine(dataDirectory, "resources.assets");
        if (!File.Exists(resourcesPath))
            throw new DirectoryNotFoundException($"ADOFAI resources.assets was not found: {resourcesPath}");

        string output = Path.GetFullPath(outputDirectory.Trim('"'));
        Directory.CreateDirectory(output);

        string classDataPath = ClassDataCache.Resolve();
        Console.WriteLine($"[extract] classdata={classDataPath}");

        var manager = new AssetsManager();
        try
        {
            manager.LoadClassPackage(classDataPath);
            AssetsFileInstance instance = manager.LoadAssetsFile(resourcesPath, false);
            manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);

            AssetFileInfo materialInfo = FindNamedAsset(
                manager,
                instance,
                AssetClassID.Material,
                "FloorMeshDefault");
            AssetTypeValueField material = manager.GetBaseField(instance, materialInfo);
            Dictionary<string, long> textureRefs = ReadMaterialTextureReferences(material);

            var written = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string propertyName, string fileName) in RequiredTextures)
            {
                if (!textureRefs.TryGetValue(propertyName, out long pathId) || pathId == 0)
                    throw new InvalidDataException(
                        $"FloorMeshDefault does not reference a texture for {propertyName}.");

                string destination = Path.Combine(output, fileName);
                ExtractTexture(manager, instance, pathId, destination, propertyName);
                written[fileName] = destination;
            }

            AssetFileInfo glowInfo = FindNamedAsset(
                manager,
                instance,
                AssetClassID.Texture2D,
                "light_white");
            string glowDestination = Path.Combine(output, "glow.png");
            ExtractTexture(manager, instance, glowInfo.PathId, glowDestination, "light_white");
            written["glow.png"] = glowDestination;

            return new FloorTextureExtractionResult(
                output,
                written["tile.png"],
                written["perlin.png"],
                written["ramp.png"],
                written["glow.png"]);
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static AssetFileInfo FindNamedAsset(
        AssetsManager manager,
        AssetsFileInstance instance,
        AssetClassID classId,
        string name)
    {
        foreach (AssetFileInfo info in instance.file.GetAssetsOfType(classId))
        {
            AssetTypeValueField root = manager.GetBaseField(instance, info);
            AssetTypeValueField nameField = root["m_Name"];
            if (!nameField.IsDummy && string.Equals(nameField.AsString, name, StringComparison.Ordinal))
                return info;
        }

        throw new InvalidDataException($"{classId} named '{name}' was not found in resources.assets.");
    }

    private static Dictionary<string, long> ReadMaterialTextureReferences(AssetTypeValueField material)
    {
        AssetTypeValueField savedProperties = material["m_SavedProperties"];
        AssetTypeValueField textureEnvironments = savedProperties["m_TexEnvs"];
        AssetTypeValueField array = textureEnvironments["Array"];
        if (array.IsDummy)
            throw new InvalidDataException("FloorMeshDefault m_TexEnvs array could not be decoded.");

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (AssetTypeValueField pair in array.Children)
        {
            AssetTypeValueField nameField = pair["first"];
            AssetTypeValueField valueField = pair["second"];
            AssetTypeValueField textureField = valueField["m_Texture"];
            if (nameField.IsDummy || textureField.IsDummy)
                continue;

            int fileId = textureField["m_FileID"].AsInt;
            long pathId = textureField["m_PathID"].AsLong;
            if (fileId != 0)
                throw new InvalidDataException(
                    $"FloorMeshDefault texture '{nameField.AsString}' points to external fileId={fileId}; this extractor currently expects resources.assets-local textures.");

            result[nameField.AsString] = pathId;
        }

        return result;
    }

    private static void ExtractTexture(
        AssetsManager manager,
        AssetsFileInstance instance,
        long pathId,
        string destination,
        string propertyName)
    {
        AssetFileInfo? info = instance.file.GetAssetInfo(pathId);
        if (info is null)
            throw new InvalidDataException(
                $"Texture referenced by {propertyName} was not found: pathId={pathId}.");
        if ((AssetClassID)info.TypeId != AssetClassID.Texture2D)
            throw new InvalidDataException(
                $"Texture referenced by {propertyName} is not Texture2D: pathId={pathId}, type={(AssetClassID)info.TypeId}.");

        AssetTypeValueField root = manager.GetBaseField(instance, info);
        TextureFile texture = TextureFile.ReadTextureFile(root);
        byte[] encoded = texture.FillPictureData(instance)
            ?? throw new InvalidDataException(
                $"Texture data for {propertyName} could not be read from {texture.m_StreamData.path}.");
        if (encoded.Length == 0)
            throw new InvalidDataException($"Texture data for {propertyName} is empty.");

        byte[] decoded = texture.DecodeTextureRaw(encoded, useBgra: false)
            ?? throw new NotSupportedException(
                $"Texture format {texture.m_TextureFormat} for {propertyName} could not be decoded.");
        if (decoded.Length == 0)
            throw new InvalidDataException($"Decoded texture for {propertyName} is empty.");

        Console.WriteLine(
            $"[extract] {propertyName} pathId={pathId} name={texture.m_Name} {texture.m_Width}x{texture.m_Height} format={texture.m_TextureFormat}");

        // Unity texture data is bottom-up relative to normal PNG scanline order.
        PngWriter.WriteRgba32(
            destination,
            texture.m_Width,
            texture.m_Height,
            decoded,
            flipVertically: true);
    }
}
