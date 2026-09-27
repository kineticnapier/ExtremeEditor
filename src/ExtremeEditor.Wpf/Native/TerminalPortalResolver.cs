using System.IO;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf.Native;

internal static class TerminalPortalResolver
{
    public static void Apply(NativeFloor[] floors, List<NativeIconAsset> iconAssets)
    {
        if (floors.Length == 0)
            return;

        string imageCandidate = IconAssetCache.FloorPath("Portal");
        if (!File.Exists(imageCandidate))
            return;

        string imagePath = Path.GetFullPath(imageCandidate);
        NativeIconAsset? asset = iconAssets.FirstOrDefault(candidate =>
            candidate.OutlinePath is null &&
            string.Equals(candidate.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            int reusableIndex = iconAssets.FindIndex(candidate =>
                string.Equals(candidate.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase) &&
                !IsUsedByNonTerminalFloor(candidate.Id, floors));
            if (reusableIndex >= 0)
            {
                NativeIconAsset reusable = iconAssets[reusableIndex];
                asset = reusable with { OutlinePath = null };
                iconAssets[reusableIndex] = asset;
            }
            else
            {
                uint iconId = checked((uint)iconAssets.Count);
                asset = NativeIconAsset.Create(iconId, imagePath, null);
                iconAssets.Add(asset);
            }
        }

        ref NativeFloor terminal = ref floors[^1];
        terminal.IconId = asset.Id;
        terminal.IconFlags = NativeFloor.IconFlagFloor;
        terminal.IconAngle = 0f;
    }

    private static bool IsUsedByNonTerminalFloor(uint iconId, NativeFloor[] floors)
    {
        for (int floor = 0; floor < floors.Length - 1; floor++)
        {
            if (floors[floor].IconId == iconId)
                return true;
        }

        return false;
    }
}
