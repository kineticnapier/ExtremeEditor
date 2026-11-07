using System.IO;

namespace ExtremeEditor.Wpf.Tests;

// Regression for #71: width-only resizes must not resend the entire camera timeline.
internal static class CameraTimelineUploadRegression
{
    internal static void Run()
    {
        string source = ReadSource("src/ExtremeEditor.Wpf/Native/NativeLevelViewport.Preparation.cs");
        int start = source.IndexOf("private void UploadPendingCameraTimeline()", StringComparison.Ordinal);
        int next = source.IndexOf("private void UploadPendingFlashTimeline()", start, StringComparison.Ordinal);
        Require(start >= 0 && next > start, "Camera timeline upload method must exist.");
        string method = source[start..next];
        int guard = method.IndexOf("ReferenceEquals(_cameraUploadSession, _session)", StringComparison.Ordinal);
        int heightGuard = method.IndexOf("_cameraUploadedHeight == viewportHeight", StringComparison.Ordinal);
        int transfer = method.IndexOf("_session.SetCameraTimeline(", StringComparison.Ordinal);
        int commit = method.LastIndexOf("_cameraUploadedHeight = viewportHeight;", StringComparison.Ordinal);
        Require(guard >= 0 && heightGuard > guard && transfer > heightGuard,
            "Native camera upload should skip unchanged timeline/session/pixel-height before transfer.");
        Require(commit > transfer,
            "Camera upload cache must be updated only after native upload succeeds.");
        int resize = source.IndexOf("private void ResizeNativeChild()", StringComparison.Ordinal);
        Require(resize >= 0 &&
                source[resize..].Contains("UploadPendingCameraTimeline();", StringComparison.Ordinal),
            "Height changes must still trigger a camera zoom update.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string file = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file))
                return File.ReadAllText(file);
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"Source file not found: {relativePath}");
    }
}
