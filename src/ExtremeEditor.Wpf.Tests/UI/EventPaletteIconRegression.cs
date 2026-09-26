using System.IO;
using ExtremeEditor.Rendering;

namespace ExtremeEditor.Wpf.Tests;

internal static class EventPaletteIconRegression
{
    public static void Run()
    {
        string quickPicker = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventQuickPicker.cs"));
        string readability = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventPaletteReadability.cs"));

        string canonicalPath = IconAssetCache.EventPath("SetSpeed");
        string expectedSuffix = Path.Combine("icons", "events", "SetSpeed.png");
        if (!canonicalPath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Event icons must use the canonical icons/events asset path.");

        if (!quickPicker.Contains("IconAssetCache.EventPath(eventType)", StringComparison.Ordinal) ||
            !quickPicker.Contains("WpfIconRenderer.GetCachedBitmap", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "RED: Add Event buttons must load the canonical ADOFAI event icon through IconAssetCache.EventPath(eventType).");
        }

        if (!quickPicker.Contains("ShortEventName(eventType)", StringComparison.Ordinal))
            throw new InvalidOperationException("RED: missing event icons must retain the ShortEventName fallback path.");

        string combinedSource = quickPicker + readability;
        string[] forbiddenBadgeContracts =
        [
            "PaletteKeyBadge",
            "keyBadge",
            "Text = digit.ToString()"
        ];
        foreach (string forbidden in forbiddenBadgeContracts)
        {
            if (combinedSource.Contains(forbidden, StringComparison.Ordinal))
                throw new InvalidOperationException($"RED: shortcut number badge UI must be removed ({forbidden}).");
        }

        if (readability.Contains("SplitPascalCase(eventType)", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "RED: the readability Loaded handler must not replace event icons with a text-only label.");
        }

        string[] shortcutContracts =
        [
            "TryAddNumberedEvent(digit)",
            "int slot = digit == 0 ? 9 : digit - 1;",
            "int index = _eventPageIndex * 10 + slot;"
        ];
        foreach (string contract in shortcutContracts)
        {
            if (!quickPicker.Contains(contract, StringComparison.Ordinal))
                throw new InvalidOperationException($"The 1-0 event shortcut contract changed: {contract}");
        }
    }

    private static string FindSourceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Unable to locate {relativePath}.");
    }
}
