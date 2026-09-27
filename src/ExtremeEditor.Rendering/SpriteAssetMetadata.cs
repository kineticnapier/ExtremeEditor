using System.Text.Json;

namespace ExtremeEditor.Rendering;

public sealed record SpriteAssetMetadata(
    float SpriteRectX,
    float SpriteRectY,
    float SpriteRectWidth,
    float SpriteRectHeight,
    float TextureRectX,
    float TextureRectY,
    float TextureRectWidth,
    float TextureRectHeight,
    float PivotX,
    float PivotY,
    float PixelsPerUnit)
{
    internal bool IsValid =>
        float.IsFinite(SpriteRectX) &&
        float.IsFinite(SpriteRectY) &&
        float.IsFinite(SpriteRectWidth) && SpriteRectWidth > 0f &&
        float.IsFinite(SpriteRectHeight) && SpriteRectHeight > 0f &&
        float.IsFinite(TextureRectX) &&
        float.IsFinite(TextureRectY) &&
        float.IsFinite(TextureRectWidth) && TextureRectWidth > 0f &&
        float.IsFinite(TextureRectHeight) && TextureRectHeight > 0f &&
        float.IsFinite(PivotX) &&
        float.IsFinite(PivotY) &&
        float.IsFinite(PixelsPerUnit) && PixelsPerUnit > 0f;

    internal static bool TryRead(string imagePath, out SpriteAssetMetadata? metadata)
    {
        metadata = null;
        string metadataPath = IconAssetCache.SpriteMetadataPath(imagePath);
        if (!File.Exists(metadataPath))
            return false;

        try
        {
            metadata = JsonSerializer.Deserialize<SpriteAssetMetadata>(
                File.ReadAllBytes(metadataPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (metadata?.IsValid == true)
                return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        metadata = null;
        return false;
    }
}
