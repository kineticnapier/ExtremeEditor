using System.Numerics;
using ExtremeEditor.Core;
using ExtremeEditor.Rendering;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class TerminalPortalRegression
{
    public static void Run()
    {
        string portalPath = IconAssetCache.FloorPath("Portal");
        string outlinePath = IconAssetCache.OutlinePath("Portal");
        string? portalDirectory = Path.GetDirectoryName(portalPath);
        string? outlineDirectory = Path.GetDirectoryName(outlinePath);
        if (string.IsNullOrWhiteSpace(portalDirectory) || string.IsNullOrWhiteSpace(outlineDirectory))
            throw new InvalidOperationException("Portal icon cache path has no parent directory.");

        byte[]? portalBackup = File.Exists(portalPath) ? File.ReadAllBytes(portalPath) : null;
        byte[]? outlineBackup = File.Exists(outlinePath) ? File.ReadAllBytes(outlinePath) : null;
        bool portalDirectoryExisted = Directory.Exists(portalDirectory);
        bool outlineDirectoryExisted = Directory.Exists(outlineDirectory);

        Directory.CreateDirectory(portalDirectory);
        Directory.CreateDirectory(outlineDirectory);
        try
        {
            // Snapshot building only consumes the path. Deliberately provide an
            // outline too: the terminal portal must opt out of floor-icon outlines.
            File.WriteAllBytes(portalPath, [0x50, 0x4F, 0x52, 0x54, 0x41, 0x4C]);
            File.WriteAllBytes(outlinePath, [0x4F, 0x55, 0x54, 0x4C, 0x49, 0x4E, 0x45]);

            LevelDocument level = LevelDocument.CreateSynthetic(3);
            VerifySnapshot(
                "normal",
                level,
                NativeLevelSnapshotBuilder.Build(level),
                portalPath);
            VerifySnapshot(
                "flat",
                level,
                FlatNativeLevelSnapshotBuilder.BuildProfiled(level).Snapshot,
                portalPath);
        }
        finally
        {
            RestoreFile(portalPath, portalBackup);
            RestoreFile(outlinePath, outlineBackup);
            RemoveDirectoryIfCreatedAndEmpty(portalDirectory, portalDirectoryExisted);
            RemoveDirectoryIfCreatedAndEmpty(outlineDirectory, outlineDirectoryExisted);
        }
    }

    private static void VerifySnapshot(
        string builderName,
        LevelDocument level,
        NativeLevelSnapshot snapshot,
        string portalPath)
    {
        if (snapshot.Floors.Length != level.FloorCount || snapshot.Floors.Length < 2)
            throw new InvalidOperationException($"RED: {builderName} snapshot floor count is invalid.");

        for (int floor = 0; floor < snapshot.Floors.Length - 1; floor++)
        {
            if (snapshot.Floors[floor].IconId != NativeFloor.NoIcon)
            {
                throw new InvalidOperationException(
                    $"RED: {builderName} non-terminal floor {floor} must not receive the terminal Portal icon.");
            }
        }

        int terminalIndex = snapshot.Floors.Length - 1;
        NativeFloor terminal = snapshot.Floors[terminalIndex];
        if (terminal.IconId == NativeFloor.NoIcon)
        {
            throw new InvalidOperationException(
                $"RED: {builderName} final floor must receive the terminal Portal icon.");
        }

        if (terminal.IconId >= snapshot.IconAssets.Length)
            throw new InvalidOperationException($"RED: {builderName} terminal Portal icon id is out of range.");

        NativeIconAsset asset = snapshot.IconAssets[terminal.IconId];
        string expectedPortalPath = Path.GetFullPath(portalPath);
        if (!string.Equals(asset.ImagePath, expectedPortalPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"RED: {builderName} terminal floor must use IconAssetCache.FloorPath(\"Portal\").");
        }

        if (asset.OutlinePath is not null)
            throw new InvalidOperationException($"RED: {builderName} terminal Portal icon must not use an outline asset.");

        if ((terminal.IconFlags & NativeFloor.IconFlagFloor) == 0u)
            throw new InvalidOperationException($"RED: {builderName} terminal Portal icon must use floor-icon rendering semantics.");

        if (MathF.Abs(terminal.IconAngle) > 0.000001f)
            throw new InvalidOperationException($"RED: {builderName} terminal Portal icon angle must remain zero.");

        Vector2 expectedPosition = level.Positions[terminalIndex];
        if (MathF.Abs(terminal.X - expectedPosition.X) > 0.000001f ||
            MathF.Abs(terminal.Y - expectedPosition.Y) > 0.000001f)
        {
            throw new InvalidOperationException(
                $"RED: {builderName} terminal Portal must be an overlay and must not move the final floor geometry.");
        }

        NativeFloor previous = snapshot.Floors[terminalIndex - 1];
        if (terminal.GeometryId != previous.GeometryId)
        {
            throw new InvalidOperationException(
                $"RED: {builderName} terminal Portal must not replace the final floor geometry.");
        }
    }

    private static void RestoreFile(string path, byte[]? backup)
    {
        if (backup is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        File.WriteAllBytes(path, backup);
    }

    private static void RemoveDirectoryIfCreatedAndEmpty(string path, bool existedBefore)
    {
        if (!existedBefore && Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }
}
