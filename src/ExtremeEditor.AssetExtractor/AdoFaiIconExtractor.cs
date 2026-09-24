using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

    private readonly record struct ResourceRef(int FileId, long PathId);

    public static IconExtractionResult Extract(string gameRoot, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string root = Path.GetFullPath(gameRoot.Trim('"'));
        string dataDirectory = Path.Combine(root, "A Dance of Fire and Ice_Data");
        string managedDirectory = Path.Combine(dataDirectory, "Managed");
        string resourcesPath = Path.Combine(dataDirectory, "resources.assets");
        string assemblyPath = Path.Combine(managedDirectory, "Assembly-CSharp.dll");
        if (!File.Exists(resourcesPath))
            throw new DirectoryNotFoundException($"ADOFAI resources.assets was not found: {resourcesPath}");
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("Assembly-CSharp.dll was not found.", assemblyPath);
        if (!Directory.Exists(managedDirectory))
            throw new DirectoryNotFoundException($"ADOFAI Managed directory was not found: {managedDirectory}");

        string output = Path.GetFullPath(outputDirectory.Trim('"'));
        string floorDirectory = Path.Combine(output, "floors");
        string outlineDirectory = Path.Combine(output, "outlines");
        string eventDirectory = Path.Combine(output, "events");
        string categoryDirectory = Path.Combine(output, "categories");
        Directory.CreateDirectory(floorDirectory);
        Directory.CreateDirectory(outlineDirectory);
        Directory.CreateDirectory(eventDirectory);
        Directory.CreateDirectory(categoryDirectory);

        string classDataPath = ClassDataCache.Resolve();
        Console.WriteLine($"[icons] classdata={classDataPath}");

        var manager = new AssetsManager
        {
            MonoTempGenerator = new MonoCecilTempGenerator(managedDirectory),
            UseMonoTemplateFieldCache = true,
            UseQuickLookup = true
        };

        try
        {
            manager.LoadClassPackage(classDataPath);
            AssetsFileInstance instance = manager.LoadAssetsFile(resourcesPath, true);
            manager.LoadClassDatabaseFromPackage(instance.file.Metadata.UnityVersion);

            foreach ((string spriteName, string canonicalName) in RepresentativeFloorIcons)
            {
                AssetFileInfo spriteInfo = FindNamedAsset(
                    manager,
                    instance,
                    AssetClassID.Sprite,
                    spriteName);

                string destination = Path.Combine(floorDirectory, canonicalName);
                ExtractSprite(manager, instance, spriteInfo, destination);
            }

            ExtractRdConstantsFloorIcons(
                manager,
                instance,
                floorDirectory,
                outlineDirectory);

            Dictionary<string, ResourceRef> resourceMap = ReadResourceMap(manager, instance);
            int eventFound = ExtractResourceEnumIcons(
                manager,
                instance,
                assemblyPath,
                resourceMap,
                "LevelEventType",
                "LevelEditor/LevelEvents/",
                eventDirectory);
            int categoryFound = ExtractResourceEnumIcons(
                manager,
                instance,
                assemblyPath,
                resourceMap,
                "LevelEventCategory",
                "LevelEditor/EventCategories/",
                categoryDirectory);

            int floorIconCount = CountPngs(floorDirectory);
            int outlineIconCount = CountPngs(outlineDirectory);
            int eventIconCount = CountPngs(eventDirectory);
            int categoryIconCount = CountPngs(categoryDirectory);

            Console.WriteLine(
                $"[icons] catalog floors={floorIconCount} outlines={outlineIconCount} events={eventIconCount}/{eventFound} categories={categoryIconCount}/{categoryFound} resourceEntries={resourceMap.Count}");

            return new IconExtractionResult(
                output,
                floorDirectory,
                outlineDirectory,
                eventDirectory,
                floorIconCount,
                outlineIconCount,
                eventIconCount);
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static void ExtractRdConstantsFloorIcons(
        AssetsManager manager,
        AssetsFileInstance instance,
        string floorDirectory,
        string outlineDirectory)
    {
        AssetTypeValueField? bestRoot = null;
        AssetFileInfo? bestInfo = null;
        int bestScore = 0;

        foreach (AssetFileInfo info in instance.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            try
            {
                AssetTypeValueField root = manager.GetBaseField(instance, info);
                int score = root.Children.Count(child => IsFloorSpriteField(child.FieldName));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRoot = root;
                    bestInfo = info;
                }
            }
            catch
            {
                // Not every MonoBehaviour can necessarily be reconstructed from the
                // installed managed assemblies. Only the RDConstants candidate matters.
            }
        }

        if (bestRoot is null || bestInfo is null || bestScore == 0)
        {
            Console.WriteLine("[icons] RDConstants floor-icon fields were not found.");
            return;
        }

        Console.WriteLine($"[icons] RDConstants candidate pathId={bestInfo.PathId} spriteFields={bestScore}");

        foreach (AssetTypeValueField field in bestRoot.Children)
        {
            string fieldName = field.FieldName;
            if (!IsFloorSpriteField(fieldName) || !TryReadPPtr(field, out ResourceRef spriteRef) || spriteRef.PathId == 0)
                continue;
            if (spriteRef.FileId != 0)
            {
                Console.WriteLine($"[icons] {fieldName}: external sprite fileId={spriteRef.FileId} skipped");
                continue;
            }

            AssetFileInfo? spriteInfo = instance.file.GetAssetInfo(spriteRef.PathId);
            if (spriteInfo is null || (AssetClassID)spriteInfo.TypeId != AssetClassID.Sprite)
            {
                Console.WriteLine($"[icons] {fieldName}: sprite pathId={spriteRef.PathId} missing or not Sprite");
                continue;
            }

            string? canonicalName;
            string destinationDirectory;
            if (fieldName.StartsWith("sprIcon", StringComparison.Ordinal))
            {
                canonicalName = fieldName["sprIcon".Length..];
                destinationDirectory = floorDirectory;
            }
            else if (fieldName.StartsWith("sprOutline", StringComparison.Ordinal))
            {
                canonicalName = fieldName["sprOutline".Length..];
                destinationDirectory = outlineDirectory;
            }
            else if (string.Equals(fieldName, "sprPortal", StringComparison.Ordinal))
            {
                canonicalName = "Portal";
                destinationDirectory = floorDirectory;
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(canonicalName))
                continue;

            string destination = Path.Combine(destinationDirectory, SafeName(canonicalName) + ".png");
            try
            {
                ExtractSprite(manager, instance, spriteInfo, destination);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[icons] {fieldName}: FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, ResourceRef> ReadResourceMap(
        AssetsManager manager,
        AssetsFileInstance instance)
    {
        var result = new Dictionary<string, ResourceRef>(StringComparer.OrdinalIgnoreCase);

        // Unity class id 147 = ResourceManager. Using the numeric id keeps this
        // independent of enum naming differences between AssetsTools.NET versions.
        foreach (AssetFileInfo info in instance.file.GetAssetsOfType((AssetClassID)147))
        {
            AssetTypeValueField root = manager.GetBaseField(instance, info);
            CollectResourceEntries(root, result);
        }

        return result;
    }

    private static void CollectResourceEntries(
        AssetTypeValueField field,
        Dictionary<string, ResourceRef> result)
    {
        AssetTypeValueField first = field["first"];
        AssetTypeValueField second = field["second"];
        if (!first.IsDummy &&
            !second.IsDummy &&
            first.TemplateField.ValueType == AssetValueType.String &&
            TryReadPPtr(second, out ResourceRef reference) &&
            reference.PathId != 0)
        {
            string key = NormalizeResourcePath(first.AsString);
            if (key.Length > 0)
                result[key] = reference;
        }

        foreach (AssetTypeValueField child in field.Children)
            CollectResourceEntries(child, result);
    }

    private static int ExtractResourceEnumIcons(
        AssetsManager manager,
        AssetsFileInstance instance,
        string assemblyPath,
        Dictionary<string, ResourceRef> resourceMap,
        string enumTypeName,
        string resourcePrefix,
        string destinationDirectory)
    {
        IReadOnlyList<string> enumNames = ReadEnumNames(assemblyPath, enumTypeName);
        int mapped = 0;

        foreach (string enumName in enumNames)
        {
            string resourcePath = NormalizeResourcePath(resourcePrefix + enumName);
            if (!TryFindResource(resourceMap, resourcePath, out ResourceRef spriteRef))
            {
                Console.WriteLine($"[icons] {enumTypeName}.{enumName}: resource mapping missing ({resourcePath})");
                continue;
            }

            mapped++;
            if (spriteRef.FileId != 0)
            {
                Console.WriteLine($"[icons] {enumTypeName}.{enumName}: external sprite fileId={spriteRef.FileId} skipped");
                continue;
            }

            AssetFileInfo? spriteInfo = instance.file.GetAssetInfo(spriteRef.PathId);
            if (spriteInfo is null || (AssetClassID)spriteInfo.TypeId != AssetClassID.Sprite)
            {
                Console.WriteLine($"[icons] {enumTypeName}.{enumName}: pathId={spriteRef.PathId} is missing or not Sprite");
                continue;
            }

            string destination = Path.Combine(destinationDirectory, SafeName(enumName) + ".png");
            try
            {
                ExtractSprite(manager, instance, spriteInfo, destination);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[icons] {enumTypeName}.{enumName}: FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }

        return mapped;
    }

    private static bool TryFindResource(
        Dictionary<string, ResourceRef> resourceMap,
        string requested,
        out ResourceRef reference)
    {
        if (resourceMap.TryGetValue(requested, out reference))
            return true;

        string suffix = "/" + requested;
        foreach ((string key, ResourceRef value) in resourceMap)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                reference = value;
                return true;
            }
        }

        reference = default;
        return false;
    }

    private static string NormalizeResourcePath(string value)
    {
        string result = value.Replace('\\', '/').Trim();
        const string assetsResourcesPrefix = "Assets/Resources/";
        if (result.StartsWith(assetsResourcesPrefix, StringComparison.OrdinalIgnoreCase))
            result = result[assetsResourcesPrefix.Length..];
        else if (result.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase))
            result = result["Resources/".Length..];

        string extension = Path.GetExtension(result);
        if (extension.Length > 0)
            result = result[..^extension.Length];

        return result.TrimStart('/');
    }

    private static IReadOnlyList<string> ReadEnumNames(string assemblyPath, string typeName)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();

        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            if (!string.Equals(metadata.GetString(type.Name), typeName, StringComparison.Ordinal))
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
                throw new InvalidDataException($"{typeName} was found but contained no enum literals.");
            return names;
        }

        throw new InvalidDataException($"{typeName} enum was not found in Assembly-CSharp.dll.");
    }

    private static bool TryReadPPtr(AssetTypeValueField field, out ResourceRef reference)
    {
        AssetTypeValueField fileIdField = field["m_FileID"];
        AssetTypeValueField pathIdField = field["m_PathID"];
        if (fileIdField.IsDummy || pathIdField.IsDummy)
        {
            reference = default;
            return false;
        }

        reference = new ResourceRef(fileIdField.AsInt, pathIdField.AsLong);
        return true;
    }

    private static bool IsFloorSpriteField(string name)
        => name.StartsWith("sprIcon", StringComparison.Ordinal) ||
           name.StartsWith("sprOutline", StringComparison.Ordinal) ||
           string.Equals(name, "sprPortal", StringComparison.Ordinal);

    private static int CountPngs(string directory)
        => Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly).Count();

    private static string SafeName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
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
