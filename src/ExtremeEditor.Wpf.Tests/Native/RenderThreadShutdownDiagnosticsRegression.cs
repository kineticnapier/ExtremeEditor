using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class RenderThreadShutdownDiagnosticsRegression
{
    internal static void Run()
    {
        string root = FindRepositoryRoot();
        string renderer = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "renderer.cpp"));
        string backend = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "d2d_backend.cpp"));
        string phases = File.ReadAllText(Path.Combine(
            root, "src", "ExtremeEditor.NativeRenderer", "src", "shutdown_diagnostics.h"));

        int shutdown = renderer.IndexOf("backend.Shutdown();", renderer.IndexOf(
            "\"RenderLoop.stopObserved\"", StringComparison.Ordinal), StringComparison.Ordinal);
        int comBegin = renderer.IndexOf("\"RenderLoop.coUninitialize.begin\"", StringComparison.Ordinal);
        int comEnd = renderer.IndexOf("\"RenderLoop.coUninitialize.end\"", StringComparison.Ordinal);
        Require(shutdown >= 0 && comBegin > shutdown && comEnd > comBegin,
            "The render thread must log entry and exit of CoUninitialize after D2D shutdown.");

        int destructorStart = backend.IndexOf("D2DBackend::~D2DBackend()", StringComparison.Ordinal);
        int destructorEnd = backend.IndexOf("bool D2DBackend::Initialize(", StringComparison.Ordinal);
        Require(destructorStart >= 0 && destructorEnd > destructorStart &&
                backend.AsSpan(destructorStart, destructorEnd - destructorStart)
                    .Contains("\"D2DBackend.destructor.begin\"".AsSpan(), StringComparison.Ordinal) &&
                backend.AsSpan(destructorStart, destructorEnd - destructorStart)
                    .Contains("\"D2DBackend.destructor.end\"".AsSpan(), StringComparison.Ordinal),
            "The render thread must log the backend destructor separately from Shutdown.");

        Require(phases.Contains("RenderLoopCoUninitializeEntry", StringComparison.Ordinal) &&
                phases.Contains("RenderLoopCoUninitializeExit", StringComparison.Ordinal) &&
                phases.Contains("D2DBackendDestructorEntry", StringComparison.Ordinal) &&
                phases.Contains("D2DBackendDestructorExit", StringComparison.Ordinal),
            "Each post-D2D shutdown phase must have its own one-shot diagnostic bit.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(
                    directory.FullName, "src", "ExtremeEditor.NativeRenderer", "src")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("RED setup failure: repository root unavailable.");
    }
}
