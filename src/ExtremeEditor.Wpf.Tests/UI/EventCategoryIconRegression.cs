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
    private static readonly (string Name, string AssetKey, string Glyph, int EventCount)[] ExpectedCategories =
    [
        ("Gameplay", "Gameplay", "◉", 11),
        ("TrackFx", "TrackFx", "▦", 7),
        ("DecorationFx", "DecorationFx", "◆", 9),
        ("VisualFx", "VisualFx", "◫", 11),
        ("FxModifiers", "FxModifiers", "⚙", 3),
        ("Jank", "Jank", "+", 10),
        ("Conveniences", "Conveniences", "★", 4),
        ("Favorites", "Favorites", "☆", 0)
    ];

    public static void Run()
    {
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
                actualEvents.Length != expected.EventCount)
            {
                throw new InvalidOperationException(
                    $"Event category order, fallback glyphs, or filtering membership changed at index {i}.");
            }
        }

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
}
