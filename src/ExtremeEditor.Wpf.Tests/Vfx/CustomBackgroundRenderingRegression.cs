using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

// RED contract: verifies production wiring, not just the existence of ABI tokens.
// The existing CustomBackgroundRegression remains unchanged.
internal static class CustomBackgroundRenderingRegression
{
    public static void Run()
    {
        var failures = new List<string>();
        Assembly assembly = typeof(MainWindow).Assembly;
        Type viewport = assembly.GetType("ExtremeEditor.Wpf.Native.NativeLevelViewport")
            ?? throw new InvalidOperationException("NativeLevelViewport is missing.");
        Type backgroundEvent = assembly.GetType("ExtremeEditor.Wpf.Native.NativeBackgroundEvent")
            ?? throw new InvalidOperationException("NativeBackgroundEvent is missing.");

        Check(failures, "managed/native ABI", () =>
        {
            // The time-line array must use blittable ABI fields; managed strings and
            // nested managed strings cannot be passed to a native EeBackgroundEvent*.
            Require(!ContainsManagedReferences(backgroundEvent, new HashSet<Type>()),
                "NativeBackgroundEvent contains managed references; native array upload is unsafe.");
        });

        string root = FindRepositoryRoot();
        string viewportSource = File.ReadAllText(Path.Combine(root,
            "src", "ExtremeEditor.Wpf", "Native", "NativeLevelViewport.Preparation.cs"));
        string viewportRuntime = File.ReadAllText(Path.Combine(root,
            "src", "ExtremeEditor.Wpf", "Native", "NativeLevelViewport.cs"));
        Check(failures, "playback upload", () =>
        {
            Require(viewportSource.Contains("UploadPendingBackgroundTimeline(", StringComparison.Ordinal) &&
                    viewportRuntime.Contains("UploadPendingBackgroundTimeline(", StringComparison.Ordinal),
                "background timeline is prepared but never uploaded to the viewport renderer.");
        });

        string renderer = File.ReadAllText(Path.Combine(root,
            "src", "ExtremeEditor.NativeRenderer", "src", "renderer.cpp"));
        string backend = File.ReadAllText(Path.Combine(root,
            "src", "ExtremeEditor.NativeRenderer", "src", "d2d_backend.cpp"));
        Check(failures, "background asset", () =>
        {
            Require(!renderer.Contains("(void)asset_id;", StringComparison.Ordinal) &&
                    !renderer.Contains("(void)image_path;", StringComparison.Ordinal),
                "background asset upload still returns the placeholder failure.");
            Require(backend.Contains("LoadBitmap(", StringComparison.Ordinal) &&
                    backend.Contains("background_bitmaps_", StringComparison.Ordinal),
                "background asset is not decoded/cached by the native renderer.");
        });

        Check(failures, "background image compositing", () =>
        {
            int start = backend.IndexOf("void D2DBackend::DrawBackground(", StringComparison.Ordinal);
            int end = start < 0 ? -1 : backend.IndexOf("\n}", start, StringComparison.Ordinal);
            Require(start >= 0 && end > start, "DrawBackground body is missing.");
            string body = backend.Substring(start, end - start);
            Require(body.Contains("DrawBitmap(", StringComparison.Ordinal),
                "DrawBackground only clears the color; no image is composited.");
        });

        Check(failures, "CustomBackground image edits reload native assets", () =>
        {
            var before = new LevelAction(1, "CustomBackground", true, null, null, null, null)
            {
                SourceProperties = new JsonObject { ["bgImage"] = "first.png" }
            };
            var changed = before with
            {
                PropertyOverrides = new JsonObject { ["bgImage"] = "second.png" }
            };
            Require(BackgroundAssetResolver.RequiresReload(before, changed),
                "changing bgImage must rebuild the native background asset table.");
            var colorOnly = before with
            {
                PropertyOverrides = new JsonObject { ["bgImage"] = "first.png", ["color"] = "abcdef" }
            };
            Require(!BackgroundAssetResolver.RequiresReload(before, colorOnly),
                "color-only changes must not trigger a full level rebuild.");
            var inactive = before with { Active = false };
            Require(BackgroundAssetResolver.RequiresReload(before, inactive),
                "disabling an image-bearing background must refresh native assets.");

            string editorSource = File.ReadAllText(Path.Combine(root,
                "src", "ExtremeEditor.Wpf", "MainWindow", "MainWindow.EventAutoApply.cs"));
            Require(editorSource.Contains(
                    "BackgroundAssetResolver.RequiresReload(before, after)", StringComparison.Ordinal),
                "event edits must check whether background assets changed.");
        });

        string headlessSource = File.ReadAllText(Path.Combine(root,
            "src", "ExtremeEditor.Wpf", "Native", "NativeHeadlessRendererSession.cs"));
        Check(failures, "headless video export background upload", () =>
        {
            Require(headlessSource.Contains(
                "foreach (NativeBackgroundAsset asset in snapshot.BackgroundAssets)",
                StringComparison.Ordinal),
                "headless renderer does not iterate background image assets.");
            Require(headlessSource.Contains(
                "NativeRendererNative.SetBackgroundAsset(_renderer, asset.Id, asset.ImagePath)",
                StringComparison.Ordinal),
                "headless renderer does not upload background images.");
            Require(headlessSource.Contains(
                "UploadArray(prepared.BackgroundTimeline, NativeRendererNative.SetBackgroundTimeline",
                StringComparison.Ordinal),
                "headless renderer does not upload background events.");
        });

        if (failures.Count != 0)
            throw new InvalidOperationException("CustomBackground rendering RED:\n- " +
                string.Join("\n- ", failures));
    }

    private static bool ContainsManagedReferences(Type type, HashSet<Type> visited)
    {
        if (!type.IsValueType)
            return true;
        if (type.IsPrimitive || type.IsEnum || type.IsPointer)
            return false;
        if (!visited.Add(type))
            return false;
        return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => ContainsManagedReferences(field.FieldType, visited));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("repository root was not found.");
    }

    private static void Check(List<string> failures, string name, Action test)
    {
        try { test(); }
        catch (Exception ex) { failures.Add(name + ": " + ex.Message); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
