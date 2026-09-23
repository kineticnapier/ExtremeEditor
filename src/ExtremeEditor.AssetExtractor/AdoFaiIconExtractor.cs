using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace ExtremeEditor.AssetExtractor;

public sealed record IconExtractionResult(
    string OutputDirectory,
    string FloorDirectory,
    string OutlineDirectory,
    string EventDirectory,
    int FloorIconCount,
    int OutlineIconCount,
    int EventIconCount);

public static class AdoFaiIconExtractor
{
    private static readonly (string SpriteName, string CanonicalName)[] RepresentativeFloorIcons =
    [
        ("swirl_red", "SwirlRed.png"),
        ("swirl_blue", "SwirlBlue.png"),
        ("tile_rabbit_light_new0", "Rabbit.png"),
        ("tile_snail_light_new0", "Snail.png")
    ];

    public static IconExtractionResult Extract(string gameRoot, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string root = Path.GetFullPath(gameRoot.Trim('"'));
        string dataDirectory = Path.Combine(root, "A Dance of Fire and Ice_Data");
        string resourcesPath = Path.Combine(dataDirectory, "resources.assets");
        if (!File.Exists(resourcesPath))
            throw new DirectoryNotFoundException($"ADOFAI resources.assets was not found: {resourcesPath}");

        string output = Path.GetFullPath(outputDirectory.Trim('"'));
        string floorDirectory = Path.Combine(output, "floors");
        string outlineDirectory = Path.Combine(output, "outlines");
        string eventDirectory = Path.Combine(output, "events");
        Directory.CreateDirectory(floorDirectory);
        Directory.CreateDirectory(outlineDirectory);
        Directory.CreateDirectory(eventDirectory);

        string classDataPath = ClassDataCache.Resolve();
        Console.WriteLine($"[icons] classdata={classDataPath}");

        var manager = new AssetsManager();
        try
        {
            manager.LoadClassPackage(classDataPath);
            AssetsFileInstance instance = manager.LoadAssetsFile(resourcesPath, false);
            manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);

            int floorIconCount = 0;
            foreach ((string spriteName, string canonicalName) in RepresentativeFloorIcons)
            {
                AssetFileInfo spriteInfo = FindNamedAsset(
                    manager,
                    instance,
                    AssetClassID.Sprite,
                    spriteName);

                string destination = Path.Combine(floorDirectory, canonicalName);
                ExtractSprite(manager, instance, spriteInfo, destination);
                floorIconCount++;
            }

            return new IconExtractionResult(
                output,
                floorDirectory,
                outlineDirectory,
                eventDirectory,
                floorIconCount,
                OutlineIconCount: 0,
                EventIconCount: 0);
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

    private static void ExtractSprite(
        AssetsManager manager,
        AssetsFileInstance instance,
        AssetFileInfo spriteInfo,
        string destination)
    {
        AssetTypeValueField sprite = manager.GetBaseField(instance, spriteInfo);
        string spriteName = sprite["m_Name"].AsString;
        AssetTypeValueField renderData = sprite["m_RD"];
        if (renderData.IsDummy)
            throw new InvalidDataException($"Sprite '{spriteName}' has no m_RD render data.");

        AssetTypeValueField textureRef = renderData["texture"];
        if (textureRef.IsDummy)
            throw new InvalidDataException($"Sprite '{spriteName}' has no m_RD.texture reference.");

        int fileId = textureRef["m_FileID"].AsInt;
        long texturePathId = textureRef["m_PathID"].AsLong;
        if (fileId != 0)
            throw new InvalidDataException(
                $"Sprite '{spriteName}' texture points to external fileId={fileId}; this extractor currently expects resources.assets-local textures.");
        if (texturePathId == 0)
            throw new InvalidDataException($"Sprite '{spriteName}' has a null texture reference.");

        AssetFileInfo? textureInfo = instance.file.GetAssetInfo(texturePathId);
        if (textureInfo is null)
            throw new InvalidDataException(
                $"Texture for sprite '{spriteName}' was not found: pathId={texturePathId}.");
        if ((AssetClassID)textureInfo.TypeId != AssetClassID.Texture2D)
            throw new InvalidDataException(
                $"Sprite '{spriteName}' references non-Texture2D pathId={texturePathId}, type={(AssetClassID)textureInfo.TypeId}.");

        AssetTypeValueField textureRoot = manager.GetBaseField(instance, textureInfo);
        TextureFile texture = TextureFile.ReadTextureFile(textureRoot);
        byte[] encoded = texture.FillPictureData(instance)
            ?? throw new InvalidDataException(
                $"Texture data for sprite '{spriteName}' could not be read from {texture.m_StreamData.path}.");
        if (encoded.Length == 0)
            throw new InvalidDataException($"Texture data for sprite '{spriteName}' is empty.");

        byte[] decoded = texture.DecodeTextureRaw(encoded, useBgra: false)
            ?? throw new NotSupportedException(
                $"Texture format {texture.m_TextureFormat} for sprite '{spriteName}' could not be decoded.");
        int sourceStride = checked(texture.m_Width * 4);
        int requiredLength = checked(sourceStride * texture.m_Height);
        if (decoded.Length < requiredLength)
            throw new InvalidDataException(
                $"Decoded texture for sprite '{spriteName}' is too short: expected {requiredLength}, got {decoded.Length}.");

        AssetTypeValueField textureRect = renderData["textureRect"];
        if (textureRect.IsDummy)
            textureRect = sprite["m_Rect"];
        if (textureRect.IsDummy)
            throw new InvalidDataException($"Sprite '{spriteName}' has no textureRect or m_Rect.");

        int x = ClampRound(textureRect["x"].AsFloat, 0, Math.Max(0, texture.m_Width - 1));
        int y = ClampRound(textureRect["y"].AsFloat, 0, Math.Max(0, texture.m_Height - 1));
        int width = ClampRound(textureRect["width"].AsFloat, 1, texture.m_Width - x);
        int height = ClampRound(textureRect["height"].AsFloat, 1, texture.m_Height - y);

        byte[] cropped = CropRgbaBottomUp(decoded, texture.m_Width, x, y, width, height);
        PngWriter.WriteRgba32(
            destination,
            width,
            height,
            cropped,
            flipVertically: true);

        Console.WriteLine(
            $"[icons] sprite={spriteName} pathId={spriteInfo.PathId} texture={texture.m_Name} texturePathId={texturePathId} crop={x},{y} {width}x{height} -> {Path.GetFileName(destination)}");
    }

    private static int ClampRound(float value, int min, int max)
    {
        if (max < min)
            throw new InvalidDataException($"Invalid sprite crop range: {min}..{max}.");
        return Math.Clamp((int)MathF.Round(value), min, max);
    }

    private static byte[] CropRgbaBottomUp(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int x,
        int y,
        int width,
        int height)
    {
        int sourceStride = checked(sourceWidth * 4);
        int cropStride = checked(width * 4);
        byte[] result = new byte[checked(cropStride * height)];

        for (int row = 0; row < height; row++)
        {
            int sourceOffset = checked((y + row) * sourceStride + x * 4);
            int destinationOffset = row * cropStride;
            source.Slice(sourceOffset, cropStride)
                .CopyTo(result.AsSpan(destinationOffset, cropStride));
        }

        return result;
    }
}
