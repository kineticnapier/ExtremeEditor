using System.IO;
using System.Reflection;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportFfmpegEncoderRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type? encoderContract = assembly.GetType("ExtremeEditor.Wpf.Export.IVideoEncoder");
        Type? ffmpegType = assembly.GetType("ExtremeEditor.Wpf.Export.FfmpegVideoEncoder");

        if (encoderContract is null)
            throw new InvalidOperationException("VE4 IVideoEncoder is missing.");
        if (ffmpegType is null)
            throw new InvalidOperationException("VE4b FfmpegVideoEncoder is missing.");
        if (!encoderContract.IsAssignableFrom(ffmpegType))
            throw new InvalidOperationException("FfmpegVideoEncoder must implement IVideoEncoder.");

        ConstructorInfo? constructor = ffmpegType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string)],
            modifiers: null);
        if (constructor is null)
        {
            throw new InvalidOperationException(
                "FfmpegVideoEncoder must accept an ffmpeg executable path.");
        }

        VerifySourceContract();
    }

    private static void VerifySourceContract()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "FfmpegVideoEncoder.cs");
        if (!File.Exists(path))
            throw new InvalidOperationException("VE4b FfmpegVideoEncoder source is missing.");

        string source = File.ReadAllText(path);
        var failures = new List<string>();

        if (!source.Contains("ProcessStartInfo", StringComparison.Ordinal))
            failures.Add("encoder must invoke ffmpeg as a subprocess");
        if (!source.Contains("-f rawvideo", StringComparison.Ordinal))
            failures.Add("encoder must describe RGB frames as rawvideo");
        if (!source.Contains("rgb24", StringComparison.Ordinal))
            failures.Add("encoder must use RGB24 for RenderedFrame RGB bytes");
        if (!source.Contains("f32le", StringComparison.Ordinal))
            failures.Add("encoder must describe offline PCM as 32-bit float little-endian");
        if (!source.Contains("libx264", StringComparison.Ordinal))
            failures.Add("encoder must encode H.264 video");
        if (!source.Contains("aac", StringComparison.Ordinal))
            failures.Add("encoder must encode AAC audio");
        if (!source.Contains("FileStream", StringComparison.Ordinal))
            failures.Add("encoder must stream raw media to bounded temp files instead of accumulating it in memory");
        if (!source.Contains("Delete", StringComparison.Ordinal))
            failures.Add("encoder must clean up temporary raw media files");
        if (!source.Contains("ThrowIfCancellationRequested", StringComparison.Ordinal))
            failures.Add("encoder must observe cancellation");
        if (source.Contains("List<byte[]>", StringComparison.Ordinal) ||
            source.Contains("List<float>", StringComparison.Ordinal))
        {
            failures.Add("encoder must not accumulate complete media in memory");
        }

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "VE4b ffmpeg source contract failed:\n- " +
                string.Join("\n- ", failures));
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "ExtremeEditor.Wpf")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the ExtremeEditor repository root for VE4b regression.");
    }
}
