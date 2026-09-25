using System.Reflection;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf.Tests;

internal static class LegacyAssetImportRemovalRegression
{
    public static void Run()
    {
        Type assetCacheType = typeof(AssetCache);
        Type iconAssetCacheType = typeof(IconAssetCache);
        Assembly renderingAssembly = assetCacheType.Assembly;

        if (assetCacheType.GetMethod("ImportProbeFolder", BindingFlags.Public | BindingFlags.Static) is not null)
        {
            throw new InvalidOperationException(
                "Legacy AssetCache.ImportProbeFolder API still exists; assets must come only from the standalone extractor cache.");
        }

        if (iconAssetCacheType.GetMethod("ImportCatalog", BindingFlags.Public | BindingFlags.Static) is not null)
        {
            throw new InvalidOperationException(
                "Legacy IconAssetCache.ImportCatalog API still exists; icons must come only from the standalone extractor cache.");
        }

        if (renderingAssembly.GetType("ExtremeEditor.Rendering.AssetImportResult") is not null)
            throw new InvalidOperationException("Legacy AssetImportResult type still exists.");

        if (renderingAssembly.GetType("ExtremeEditor.Rendering.IconImportResult") is not null)
            throw new InvalidOperationException("Legacy IconImportResult type still exists.");

        if (assetCacheType.GetMethod("PathFor", BindingFlags.Public | BindingFlags.Static) is null)
            throw new InvalidOperationException("AssetCache.PathFor must remain as the canonical cache read API.");

        foreach (string methodName in new[] { "EventPath", "FloorPath", "OutlinePath" })
        {
            if (iconAssetCacheType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static) is null)
                throw new InvalidOperationException($"IconAssetCache.{methodName} must remain as a canonical cache read API.");
        }
    }
}
