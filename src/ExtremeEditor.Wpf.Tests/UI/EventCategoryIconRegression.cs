using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ExtremeEditor.Rendering;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class EventCategoryIconRegression
{
    private sealed record ExpectedCategory(string Name, string AssetKey, string Glyph, string[] Events);

    private static readonly ExpectedCategory[] ExpectedCategories =
    [
        new("Gameplay", "Gameplay", "◉",
        [
            "SetSpeed", "Twirl", "Multitap", "Checkpoint", "SetHitsound", "PlaySound",
            "SetPlanetRotation", "KillPlayer", "Pause", "AutoPlayTiles", "ScalePlanets"
        ]),
        new("TrackFx", "TrackFx", "▦",
        [
            "ColorTrack", "AnimateTrack", "RecolorTrack", "MoveTrack", "PositionTrack",
            "TileDimensions", "SetFloorIcon"
        ]),
        new("DecorationFx", "DecorationFx", "◆",
        [
            "MoveDecorations", "SetText", "EmitParticle", "SetParticle", "SetObject", "SetDefaultText"
        ]),
        new("VisualFx", "VisualFx", "◫",
        [
            "CustomBackground", "Flash", "MoveCamera", "SetFilter", "SetFilterAdvanced",
            "HallOfMirrors", "ShakeScreen", "Bloom", "ScreenTile", "ScreenScroll", "SetFrameRate"
        ]),
        new("FxModifiers", "FxModifiers", "⚙",
        [
            "RepeatEvents", "SetConditionalEvents", "SetInputEvent"
        ]),
        new("Conveniences", "Conveniences", "★",
        [
            "EditorComment", "Bookmark", "CallMethod", "AddComponent"
        ]),
        new("Jank", "Jank", "+",
        [
            "Hold", "SetHoldSound", "MultiPlanet", "FreeRoam", "FreeRoamTwirl",
            "FreeRoamRemove", "Hide", "ScaleMargin", "ScaleRadius"
        ]),
        new("Favorites", "Favorites", "☆", [])
    ];

    public static void Run()
    {
        VerifyPickerNavigationContract();

        Type mainWindowType = typeof(MainWindow);
        Type definitionType = mainWindowType.GetNestedType(
            "EventCategoryDefinition",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow.EventCategoryDefinition is missing.");

        FieldInfo categoriesField = mainWindowType.GetField(
            "EventCategories",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow.EventCategories is missing.");
        if (categoriesField.GetValue(null) is not IEnumerable categories)
            throw new InvalidOperationException("MainWindow.EventCategories must remain enumerable.");

        PropertyInfo nameProperty = RequireProperty(definitionType, "Name");
        PropertyInfo glyphProperty = RequireProperty(definitionType, "Glyph");
        PropertyInfo eventsProperty = RequireProperty(definitionType, "Events");
        object[] actualCategories = categories.Cast<object>().ToArray();

        if (actualCategories.Length != ExpectedCategories.Length)
            throw new InvalidOperationException("The event category count changed while adding category icons.");

        for (int i = 0; i < ExpectedCategories.Length; i++)
        {
            object category = actualCategories[i];
            var expected = ExpectedCategories[i];
            string actualName = (string)(nameProperty.GetValue(category)
                ?? throw new InvalidOperationException($"Event category {i} has no name."));
            string actualGlyph = (string)(glyphProperty.GetValue(category)
                ?? throw new InvalidOperationException($"Event category {actualName} has no fallback glyph."));
            string[] actualEvents = (string[])(eventsProperty.GetValue(category)
                ?? throw new InvalidOperationException($"Event category {actualName} has no event list."));

            if (!string.Equals(actualName, expected.Name, StringComparison.Ordinal) ||
                !string.Equals(actualGlyph, expected.Glyph, StringComparison.Ordinal) ||
                !actualEvents.SequenceEqual(expected.Events, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Event category order, fallback glyphs, or ADOFAI event ordering changed at index {i} ({expected.Name}).");
            }
        }

        string[] paletteEvents = actualCategories
            .SelectMany(category => (string[])eventsProperty.GetValue(category)!)
            .ToArray();
        if (paletteEvents.Length != 51)
            throw new InvalidOperationException($"The Add Event palette must contain exactly 51 events, not {paletteEvents.Length}.");

        string[] excludedEvents = ["AddDecoration", "AddText", "AddObject", "FreeRoamWarning"];
        string[] visibleExcludedEvents = excludedEvents
            .Where(excluded => paletteEvents.Contains(excluded, StringComparer.Ordinal))
            .ToArray();
        if (visibleExcludedEvents.Length > 0)
        {
            throw new InvalidOperationException(
                $"Decoration-only/internal events remain in the Add Event palette: {string.Join(", ", visibleExcludedEvents)}.");
        }
        if (!paletteEvents.Contains("SetFilterAdvanced", StringComparer.Ordinal))
            throw new InvalidOperationException("SetFilterAdvanced is a current ADOFAI Add Event entry and must remain visible.");

        PropertyInfo assetKeyProperty = definitionType.GetProperty(
            "AssetKey",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "EventCategoryDefinition must explicitly map each category to its canonical ADOFAI asset key.");

        MethodInfo categoryPathMethod = typeof(IconAssetCache).GetMethod(
            "CategoryPath",
            BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("IconAssetCache.CategoryPath is missing.");

        MethodInfo createContentMethod = mainWindowType.GetMethod(
            "CreateEventCategoryContent",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "MainWindow.CreateEventCategoryContent must load a category image with a safe glyph fallback.");

        for (int i = 0; i < ExpectedCategories.Length; i++)
        {
            object category = actualCategories[i];
            var expected = ExpectedCategories[i];
            string actualAssetKey = (string)(assetKeyProperty.GetValue(category)
                ?? throw new InvalidOperationException($"Event category {expected.Name} has no asset key."));
            if (!string.Equals(actualAssetKey, expected.AssetKey, StringComparison.Ordinal))
                throw new InvalidOperationException($"Event category {expected.Name} maps to the wrong asset key.");

            string canonicalPath = (string)(categoryPathMethod.Invoke(null, [actualAssetKey])
                ?? throw new InvalidOperationException($"CategoryPath returned null for {actualAssetKey}."));
            string expectedSuffix = Path.Combine("icons", "categories", expected.AssetKey + ".png");
            if (!canonicalPath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Event category {expected.Name} does not use the canonical icons/categories path.");
        }

        const string missingGlyph = "?";
        object? missingContent = createContentMethod.Invoke(null, ["__missing_category__", missingGlyph]);
        if (!string.Equals(missingContent as string, missingGlyph, StringComparison.Ordinal))
            throw new InvalidOperationException("A missing category icon must retain a visible glyph fallback.");

        string knownAssetKey = ExpectedCategories[0].AssetKey;
        string knownGlyph = ExpectedCategories[0].Glyph;
        string knownPath = (string)categoryPathMethod.Invoke(null, [knownAssetKey])!;
        object? knownContent = createContentMethod.Invoke(null, [knownAssetKey, knownGlyph]);
        if (File.Exists(knownPath) && knownContent is not Image)
            throw new InvalidOperationException("An available canonical category icon must replace the glyph content.");

        if (knownContent is Image image)
        {
            if (image.Source is null ||
                ReferenceEquals(image.ReadLocalValue(Image.SourceProperty), DependencyProperty.UnsetValue))
            {
                throw new InvalidOperationException("Category image Source must resolve before template/layout.");
            }
            if (image.Stretch != Stretch.Uniform)
                throw new InvalidOperationException("Category icons must preserve their aspect ratio.");

            var button = new Button { Content = image };
            button.ApplyTemplate();
            button.Measure(new Size(36, 31));
            button.Arrange(new Rect(0, 0, 36, 31));
            button.UpdateLayout();
        }
        else if (!string.Equals(knownContent as string, knownGlyph, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Category content must be either the canonical image or its glyph fallback.");
        }
    }

    private static PropertyInfo RequireProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{type.Name}.{name} is missing.");

    private static void VerifyPickerNavigationContract()
    {
        string source = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventQuickPicker.cs"));
        string[] requiredContracts =
        [
            "(selectedCategory.Events.Length + 9) / 10",
            "int pageStart = _eventPageIndex * 10;",
            "_eventCategoryIndex = categoryIndex;",
            "if ((uint)digit < (uint)EventCategories.Length)",
            "_eventCategoryIndex = digit;",
            "int index = _eventPageIndex * 10 + slot;"
        ];
        foreach (string contract in requiredContracts)
        {
            if (!source.Contains(contract, StringComparison.Ordinal))
                throw new InvalidOperationException($"Event picker paging/shortcut/selection contract is missing: {contract}");
        }

        if (source.CountOccurrences("_eventPageIndex = 0;") < 2)
            throw new InvalidOperationException("Mouse and Ctrl+digit category selection must both reset event paging.");
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

    private static int CountOccurrences(this string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }
}
