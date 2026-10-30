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

        VerifyAtomicCommit(ffmpegType, constructor);
        VerifySourceContract();
    }

    private static void VerifyAtomicCommit(Type encoderType, ConstructorInfo constructor)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-VideoCommit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string final = Path.Combine(directory, "result.mp4");
            string temporary = Path.Combine(directory, ".result.tmp.mp4");
            File.WriteAllText(final, "old-complete-video");

            MethodInfo commit = encoderType.GetMethod(
                "CommitVideoOutput", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Atomic video commit helper is missing.");
            FieldInfo tempField = encoderType.GetField("_videoTempPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo finalField = encoderType.GetField("_finalOutputPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object encoder = constructor.Invoke(["unused-ffmpeg.exe"]);
            tempField.SetValue(encoder, temporary);
            finalField.SetValue(encoder, final);

            try
            {
                commit.Invoke(encoder, null);
                throw new InvalidOperationException("Missing temporary video unexpectedly replaced the destination.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
            }
            if (File.ReadAllText(final) != "old-complete-video")
                throw new InvalidOperationException("Failed video commit damaged the existing destination.");

            File.WriteAllText(temporary, "new-complete-video");
            commit.Invoke(encoder, null);
            if (File.ReadAllText(final) != "new-complete-video" || File.Exists(temporary))
                throw new InvalidOperationException("Successful video commit was not an atomic replacement.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
        if (!source.Contains("_videoTempPath", StringComparison.Ordinal) ||
            !source.Contains("File.Move", StringComparison.Ordinal))
        {
            failures.Add("encoder must commit a same-directory temporary MP4 only after success");
        }
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
