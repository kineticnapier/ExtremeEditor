using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class VideoExportStreamingEncoderRegression
{
    public static void Run()
    {
        string root = FindRepoRoot();
        string encoderPath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "FfmpegVideoEncoder.cs");
        string contractPath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoEncoderContracts.cs");
        string servicePath = Path.Combine(
            root, "src", "ExtremeEditor.Wpf", "Export", "VideoExportService.cs");

        string encoder = File.ReadAllText(encoderPath);
        string contract = File.ReadAllText(contractPath);
        string service = File.ReadAllText(servicePath);

        var failures = new List<string>();

        // The MP4 temporary output path is intentional: it is atomically
        // committed after ffmpeg succeeds. Only raw RGB staging is forbidden.
        if (encoder.Contains(".rgb24", StringComparison.Ordinal) ||
            encoder.Contains("_videoStream", StringComparison.Ordinal))
        {
            failures.Add("video export must not write raw RGB frames to a temporary file");
        }
        if (!encoder.Contains("_videoTempPath", StringComparison.Ordinal))
            failures.Add("ffmpeg must retain the temporary MP4 path for atomic output commit");

        if (!encoder.Contains("RedirectStandardInput = true", StringComparison.Ordinal))
            failures.Add("ffmpeg must receive video frames through redirected stdin");
        if (!encoder.Contains("StandardInput.BaseStream", StringComparison.Ordinal))
            failures.Add("WriteVideoFrameAsync must stream RGB24 directly to ffmpeg stdin");
        if (!encoder.Contains(".f32le", StringComparison.Ordinal))
            failures.Add("audio may remain in a bounded temporary f32le file");
        if (!encoder.Contains("TryKill", StringComparison.Ordinal))
            failures.Add("cancellation must still terminate ffmpeg");
        if (encoder.Contains("List<byte[]>", StringComparison.Ordinal) ||
            encoder.Contains("List<RenderedFrame>", StringComparison.Ordinal))
        {
            failures.Add("encoder must not accumulate rendered frames in memory");
        }

        if (!contract.Contains("BeginVideoAsync", StringComparison.Ordinal))
            failures.Add("IVideoEncoder must expose an explicit BeginVideoAsync phase");
        if (!service.Contains("WriteAudioChunkAsync", StringComparison.Ordinal) ||
            !service.Contains("BeginVideoAsync", StringComparison.Ordinal) ||
            !service.Contains("WriteVideoFrameAsync", StringComparison.Ordinal))
        {
            failures.Add("export service must render audio first, then start ffmpeg video streaming");
        }
        else
        {
            int audio = service.IndexOf("WriteAudioChunkAsync", StringComparison.Ordinal);
            int beginVideo = service.IndexOf("BeginVideoAsync", StringComparison.Ordinal);
            int video = service.IndexOf("WriteVideoFrameAsync", StringComparison.Ordinal);
            if (!(audio < beginVideo && beginVideo < video))
            {
                failures.Add("export service phase order must be audio -> BeginVideoAsync -> video");
            }
        }

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Video export streaming encoder contract failed:\n- " +
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
            "Could not locate the ExtremeEditor repository root for streaming encoder regression.");
    }
}
