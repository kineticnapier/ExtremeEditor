using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class EditorSelectionVisualizationRegression
{
    internal static void Run()
    {
        string root = FindRepositoryRoot();
        string renderer = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "renderer.cpp"));
        string backendHeader = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "d2d_backend.h"));
        string backend = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "d2d_backend.cpp"));
        string cameraBackend = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "d2d_backend_camera.cpp"));
        string compatibilityBackend = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "d2d_backend_compat.cpp"));

        var failures = new List<string>();

        // SetSelection already retains the complete set. The renderer-facing frame
        // snapshot must retain it as well instead of reducing it to selected_floor_.
        if (!renderer.Contains("selected_floors = selected_floors_", StringComparison.Ordinal) &&
            !renderer.Contains("selected_floors_snapshot = selected_floors_", StringComparison.Ordinal))
        {
            failures.Add("selected range is not retained by the renderer-facing frame snapshot");
        }

        if (!backendHeader.Contains("selected_floors", StringComparison.Ordinal) ||
            !backend.Contains("selected_floors", StringComparison.Ordinal) ||
            !cameraBackend.Contains("selected_floors", StringComparison.Ordinal))
        {
            failures.Add("selected range is not supplied to both normal and camera overlay paths");
        }

        string allBackendSources = backendHeader + backend + cameraBackend;
        if (!allBackendSources.Contains("selection_range_brush_", StringComparison.Ordinal) ||
            !allBackendSources.Contains("selection_primary_brush_", StringComparison.Ordinal))
        {
            failures.Add("primary and range members do not have distinct renderer styles");
        }

        // Selection overlays must remain outside the edit-only HUD condition so
        // playback planets do not make the selection state disappear.
        int overlayCall = compatibilityBackend.IndexOf("DrawSceneOverlaysCamera(", StringComparison.Ordinal);
        int editOnlyHud = compatibilityBackend.IndexOf("if (!playback.active)", StringComparison.Ordinal);
        if (overlayCall < 0 || editOnlyHud < 0 || overlayCall > editOnlyHud)
            failures.Add("selection overlay is not preserved during playback");

        // Existing camera-aware overlay already follows floor rotation and
        // MoveTrack scale. Keep these as PASS contracts for the GREEN change.
        if (!cameraBackend.Contains("camera_rotation", StringComparison.Ordinal) ||
            !cameraBackend.Contains("floor.transform_scale_x", StringComparison.Ordinal) ||
            !cameraBackend.Contains("floor.transform_scale_y", StringComparison.Ordinal))
        {
            failures.Add("selection overlay does not follow camera rotation and transformed floor scale");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Selection visualization contract failed: " + string.Join("; ", failures) + ".");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExtremeEditor.sln")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Unable to locate the repository root from {AppContext.BaseDirectory}.");
    }
}
