using System.Reflection;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeOnlyViewportRegression
{
    public static void Run()
    {
        Type windowType = typeof(MainWindow);
        FieldInfo[] fields = windowType.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        FieldInfo? legacyViewport = fields.FirstOrDefault(field =>
            typeof(LevelViewport).IsAssignableFrom(field.FieldType));
        if (legacyViewport is not null)
        {
            throw new InvalidOperationException(
                $"MainWindow still owns WPF LevelViewport field '{legacyViewport.Name}'; the production UI must be native-only.");
        }

        if (fields.Any(field => string.Equals(field.Name, "Viewport", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "MainWindow still has the legacy XAML 'Viewport' field.");
        }

        FieldInfo? nativeViewport = fields.FirstOrDefault(field =>
            typeof(NativeLevelViewport).IsAssignableFrom(field.FieldType));
        if (nativeViewport is null)
            throw new InvalidOperationException("MainWindow must own NativeLevelViewport as its production viewport.");
        if (!string.Equals(nativeViewport.Name, "NativeViewport", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The production native viewport field must remain named 'NativeViewport', actual '{nativeViewport.Name}'.");
        }

        foreach (string legacyUiField in new[] { "NativeViewportToggle", "FloorPreviewToggle" })
        {
            if (fields.Any(field => string.Equals(field.Name, legacyUiField, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Legacy dual-viewport UI field '{legacyUiField}' still exists.");
            }
        }

        foreach (string legacyHandler in new[]
                 {
                     "FloorPreviewChanged",
                     "ViewportFollowPlayerChanged",
                     "NativeViewportSelectedFloorChanged",
                     "NativeViewportToggleChanged",
                     "ViewportSelectionChanged"
                 })
        {
            if (windowType.GetMethod(
                    legacyHandler,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null)
            {
                throw new InvalidOperationException(
                    $"Legacy WPF/dual-viewport handler '{legacyHandler}' still exists on MainWindow.");
            }
        }

        Type selectionType = windowType.Assembly.GetType("ExtremeEditor.Wpf.EditorSelectionState")
            ?? throw new InvalidOperationException("EditorSelectionState is missing.");
        if (!fields.Any(field => selectionType.IsAssignableFrom(field.FieldType)))
        {
            throw new InvalidOperationException(
                "MainWindow must keep EditorSelectionState as the viewport-independent selection source of truth.");
        }

        RequireNativeMethod(nameof(NativeLevelViewport.SetLevel), typeof(LevelDocument));
        RequireNativeMethod(nameof(NativeLevelViewport.SetSelection), typeof(IEnumerable<int>), typeof(int));
        RequireNativeMethod(
            nameof(NativeLevelViewport.SetPlaybackState),
            typeof(double),
            typeof(double),
            typeof(bool),
            typeof(bool));
        RequireNativeMethod(nameof(NativeLevelViewport.ClearPlayback));
        RequireNativeMethod(nameof(NativeLevelViewport.FrameAll));
        RequireNativeMethod("ReloadAssets");

        VerifyNativeResizeDeduplication();
        TerminalPortalRegression.Run();
    }

    private static void VerifyNativeResizeDeduplication()
    {
        uint width = NativeLevelViewport.ToPixelExtent(800.1, 1.25);
        uint sameWidth = NativeLevelViewport.ToPixelExtent(800.2, 1.25);
        if (width != sameWidth)
            throw new InvalidOperationException("The DPI resize test requires equal physical widths.");

        if (NativeLevelViewport.ShouldResizeNativeChild(
                sameSession: true, width, 700, sameWidth, 700))
            throw new InvalidOperationException(
                "An unchanged physical viewport size must not trigger native Resize.");

        if (!NativeLevelViewport.ShouldResizeNativeChild(
                sameSession: true, width, 700, width + 1, 700) ||
            !NativeLevelViewport.ShouldResizeNativeChild(
                sameSession: true, width, 700, width, 701))
            throw new InvalidOperationException(
                "A changed physical viewport dimension must trigger native Resize.");

        if (!NativeLevelViewport.ShouldResizeNativeChild(
                sameSession: false, width, 700, width, 700))
            throw new InvalidOperationException(
                "A replacement native session must receive its own viewport size.");
    }

    private static void RequireNativeMethod(string name, params Type[] parameterTypes)
    {
        MethodInfo? method = typeof(NativeLevelViewport).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        if (method is null)
        {
            throw new InvalidOperationException(
                $"NativeLevelViewport.{name}({string.Join(", ", parameterTypes.Select(type => type.Name))}) is required by the native-only production path.");
        }
    }
}
