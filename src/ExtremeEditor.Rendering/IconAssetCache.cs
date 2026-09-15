using System.IO.Compression;

namespace ExtremeEditor.Rendering;

public sealed record IconImportResult(
    string CacheDirectory,
    int EventIcons,
    int FloorIcons,
    int OutlineIcons,
    int IgnoredFiles);

public static class IconAssetCache
{
    public static string IconDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExtremeEditor", "AssetCache", "icons");

    public static string EventDirectory => Path.Combine(IconDirectory, "events");
    public static string FloorDirectory => Path.Combine(IconDirectory, "floors");
    public static string OutlineDirectory => Path.Combine(IconDirectory, "outlines");

    public static IconImportResult ImportCatalog(string sourcePath)
    {
        Directory.CreateDirectory(EventDirectory);
        Directory.CreateDirectory(FloorDirectory);
        Directory.CreateDirectory(OutlineDirectory);

        int events = 0;
        int floors = 0;
        int outlines = 0;
        int ignored = 0;

        if (Directory.Exists(sourcePath))
        {
            foreach (string path in Directory.EnumerateFiles(sourcePath, "*.png", SearchOption.AllDirectories))
            {
                ImportOne(Path.GetFileName(path), () => File.OpenRead(path),
                    ref events, ref floors, ref outlines, ref ignored);
            }
        }
        else if (File.Exists(sourcePath) && string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive archive = ZipFile.OpenRead(sourcePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    continue;
                ImportOne(Path.GetFileName(entry.FullName), entry.Open,
                    ref events, ref floors, ref outlines, ref ignored);
            }
        }
        else
        {
            throw new FileNotFoundException("Select an icon-catalog-assets folder or ZIP.", sourcePath);
        }

        return new IconImportResult(IconDirectory, events, floors, outlines, ignored);
    }

    public static string EventPath(string eventType) => Path.Combine(EventDirectory, SafeName(eventType) + ".png");
    public static string FloorPath(string floorIcon) => Path.Combine(FloorDirectory, SafeName(floorIcon) + ".png");
    public static string OutlinePath(string floorIcon) => Path.Combine(OutlineDirectory, SafeName(floorIcon) + ".png");

    private static void ImportOne(
        string fileName,
        Func<Stream> open,
        ref int events,
        ref int floors,
        ref int outlines,
        ref int ignored)
    {
        if (TryExtractKey(fileName, "event-", out string? eventType))
        {
            Copy(open, EventPath(eventType));
            events++;
            return;
        }

        if (TryExtractKey(fileName, "floor-sprIcon", out string? floorIcon))
        {
            Copy(open, FloorPath(floorIcon));
            floors++;
            return;
        }

        if (TryExtractKey(fileName, "floor-sprOutline", out string? outlineIcon))
        {
            Copy(open, OutlinePath(outlineIcon));
            outlines++;
            return;
        }

        if (fileName.StartsWith("floor-sprPortal-", StringComparison.OrdinalIgnoreCase))
        {
            Copy(open, FloorPath("Portal"));
            floors++;
            return;
        }

        ignored++;
    }

    private static bool TryExtractKey(string fileName, string prefix, out string key)
    {
        key = string.Empty;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        int start = prefix.Length;
        int end = fileName.IndexOf('-', start);
        if (end <= start)
            return false;

        key = fileName[start..end];
        return key.Length > 0;
    }

    private static void Copy(Func<Stream> open, string destination)
    {
        using Stream input = open();
        using FileStream output = File.Create(destination);
        input.CopyTo(output);
    }

    private static string SafeName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
