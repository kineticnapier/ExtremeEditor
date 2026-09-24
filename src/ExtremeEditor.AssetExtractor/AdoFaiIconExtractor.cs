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

    private sealed record ResourceAsset(AssetsFileInstance Instance, AssetFileInfo Info);

    public static IconExtractionResult Extract(string gameRoot, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string root = Path.GetFullPath(gameRoot.Trim('"'));
        string dataDirectory = Path.Combine(root, "A Dance of Fire and Ice_Data");
        string managedDirectory = Path.Combine(dataDirectory, "Managed");
        string resourcesPath = Path.Combine(dataDirectory, "resources.assets");
        string globalManagersPath = Path.Combine(dataDirectory, "globalgamemanagers");
        string assemblyPath = Path.Combine(managedDirectory, "Assembly-CSharp.dll");
        if (!File.Exists(resourcesPath))
            throw new DirectoryNotFoundException($"ADOFAI resources.assets was not found: {resourcesPath}");
        if (!File.Exists(globalManagersPath))
            throw new FileNotFoundException("ADOFAI globalgamemanagers was not found.", globalManagersPath);
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
            AssetsFileInstance globalManagers = manager.LoadAssetsFile(globalManagersPath, true);
            manager.LoadClassDatabaseFromPackage(globalManagers.file.Metadata.UnityVersion);

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

            Dictionary<string, ResourceAsset> resourceMap = ReadResourceMap(manager, globalManagers);
            int eventFound = ExtractResourceEnumIcons(
                manager,
                assemblyPath,
                resourceMap,
                "LevelEventType",
                "LevelEditor/LevelEvents/",
                eventDirectory);
            int categoryFound = ExtractResourceEnumIcons(
                manager,
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
            if (!IsFloorSpriteField(fieldName))
                continue;

            AssetExternal spriteExternal;
            try
            {
                spriteExternal = manager.GetExtAsset(instance, field, onlyGetInfo: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[icons] {fieldName}: sprite reference resolve failed: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (spriteExternal.file is null || spriteExternal.info is null)
                continue;
            if ((AssetClassID)spriteExternal.info.TypeId != AssetClassID.Sprite)
            {
                Console.WriteLine($"[icons] {fieldName}: resolved asset is not Sprite ({(AssetClassID)spriteExternal.info.TypeId})");
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
                ExtractSprite(manager, spriteExternal.file, spriteExternal.info, destination);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[icons] {fieldName}: FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, ResourceAsset> ReadResourceMap(
        AssetsManager manager,
        AssetsFileInstance globalManagers)
    {
        var result = new Dictionary<string, ResourceAsset>(StringComparer.OrdinalIgnoreCase);

        // Unity serializes the Resources path table in the ResourceManager object in
        // globalgamemanagers. Its PPtrs usually point into resources.assets.
        foreach (AssetFileInfo info in globalManagers.file.GetAssetsOfType((AssetClassID)147))
        {
            AssetTypeValueField root = manager.GetBaseField(globalManagers, info);
            AssetTypeValueField container = root["m_Container.Array"];
            if (container.IsDummy)
            {
                Console.WriteLine($"[icons] ResourceManager pathId={info.PathId}: m_Container.Array missing");
                continue;
            }

            foreach (AssetTypeValueField entry in container.Children)
            {
                AssetTypeValueField first = entry["first"];
                AssetTypeValueField second = entry["second"];
                if (first.IsDummy || second.IsDummy || first.TemplateField.ValueType != AssetValueType.String)
                    continue;

                string key = NormalizeResourcePath(first.AsString);
                if (key.Length == 0)
                    continue;

                try
                {
                    AssetExternal external = manager.GetExtAsset(globalManagers, second, onlyGetInfo: true);
                    if (external.file is null || external.info is null)
                    {
                        Console.WriteLine($"[icons] ResourceManager: unresolved resource {key}");
                        continue;
                    }

                    result[key] = new ResourceAsset(external.file, external.info);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[icons] ResourceManager: failed to resolve {key}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        return result;
    }

    private static int ExtractResourceEnumIcons(
        AssetsManager manager,
        string assemblyPath,
        Dictionary<string, ResourceAsset> resourceMap,
        string enumTypeName,
        string resourcePrefix,
        string destinationDirectory)
    {
        IReadOnlyList<string> enumNames = ReadEnumNames(assemblyPath, enumTypeName);
        int mapped = 0;

        foreach (string enumName in enumNames)
        {
            string resourcePath = NormalizeResourcePath(resourcePrefix + enumName);
            if (!TryFindResource(resourceMap, resourcePath, out ResourceAsset? spriteAsset))
            {
                Console.WriteLine($"[icons] {enumTypeName}.{enumName}: resource mapping missing ({resourcePath})");
                continue;
            }

            mapped++;
            if ((AssetClassID)spriteAsset.Info.TypeId != AssetClassID.Sprite)
            {
                Console.WriteLine(
                    $"[icons] {enumTypeName}.{enumName}: resource is {(AssetClassID)spriteAsset.Info.TypeId}, not Sprite");
                continue;
            }

            string destination = Path.Combine(destinationDirectory, SafeName(enumName) + ".png");
            try
            {
                ExtractSprite(manager, spriteAsset.Instance, spriteAsset.Info, destination);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[icons] {enumTypeName}.{enumName}: FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }

        return mapped;
    }

    private static bool TryFindResource(
        Dictionary<string, ResourceAsset> resourceMap,
        string requested,
        out ResourceAsset? resource)
    {
        if (resourceMap.TryGetValue(requested, out resource))
            return true;

        string suffix = "/" + requested;
        foreach ((string key, ResourceAsset value) in resourceMap)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                resource = value;
                return true;
            }
        }

        resource = null;
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

        AssetExternal textureExternal;
        try
        {
            textureExternal = manager.GetExtAsset(instance, textureRef, onlyGetInfo: true);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Texture reference for sprite '{spriteName}' could not be resolved.", ex);
        }

        if (textureExternal.file is null || textureExternal.info is null)
            throw new InvalidDataException($"Sprite '{spriteName}' has a null or unresolved texture reference.");
        if ((AssetClassID)textureExternal.info.TypeId != AssetClassID.Texture2D)
            throw new InvalidDataException(
                $"Sprite '{spriteName}' references non-Texture2D pathId={textureExternal.info.PathId}, type={(AssetClassID)textureExternal.info.TypeId}.");

        AssetTypeValueField textureRoot = manager.GetBaseField(textureExternal.file, textureExternal.info);
        TextureFile texture = TextureFile.ReadTextureFile(textureRoot);
        byte[] encoded = texture.FillPictureData(textureExternal.file)
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
            $"[icons] sprite={spriteName} pathId={spriteInfo.PathId} texture={texture.m_Name} texturePathId={textureExternal.info.PathId} crop={x},{y} {width}x{height} -> {Path.GetFileName(destination)}");
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
