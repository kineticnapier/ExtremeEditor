using System.IO;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativePlaybackAnchorDispatchRegression
{
    internal static void Run()
    {
        var gate = new NativePlaybackAnchorDispatchGate();

        Require(gate.ShouldSend(0.0, 1.0, active: false, playing: false),
            "First stopped anchor must reach the native renderer.");
        for (int i = 0; i < 100; i++)
            Require(!gate.ShouldSend(0.0, 1.0, active: false, playing: false),
                "Identical stopped timer ticks must not re-upload native anchors.");

        Require(gate.ShouldSend(1.0, 1.0, active: true, playing: true),
            "Starting playback must forward the active state.");
        Require(gate.ShouldSend(1.0, 1.0, active: true, playing: true),
            "Playing anchors must not be deduplicated, even at equal timestamps.");
        Require(gate.ShouldSend(2.0, 1.0, active: true, playing: true),
            "Advancing playback must forward updated time.");

        Require(gate.ShouldSend(2.0, 1.0, active: true, playing: false),
            "Pausing must forward the playing-to-paused transition.");
        Require(!gate.ShouldSend(2.0, 1.0, active: true, playing: false),
            "An unchanged paused anchor must be skipped.");
        Require(gate.ShouldSend(2.5, 1.0, active: true, playing: false),
            "Seeking while paused must forward the new chart time.");
        Require(gate.ShouldSend(2.5, 0.75, active: true, playing: false),
            "Changing playback speed while paused must forward the new rate.");
        Require(gate.ShouldSend(2.5, 0.75, active: false, playing: false),
            "Stopping from a paused preview must forward the inactive state.");
        Require(!gate.ShouldSend(2.5, 0.75, active: false, playing: false),
            "Repeated inactive state must be skipped.");

        gate.Invalidate();
        Require(gate.ShouldSend(2.5, 0.75, active: false, playing: false),
            "Reloading a native scene must resend even an identical stopped anchor.");
        Require(gate.ShouldSend(2.5, 0.75, active: false, playing: true),
            "Resuming after a scene reload must forward the new state.");

        VerifyFailedAnchorUploadRetry();

        VerifyViewportWiring();
    }

    private static void VerifyFailedAnchorUploadRetry()
    {
        var gate = new NativePlaybackAnchorDispatchGate();
        var expected = new InvalidOperationException("Simulated native anchor failure");
        int uploads = 0;
        try
        {
            gate.Dispatch(3.5, 1.0, active: true, playing: false, () =>
            {
                uploads++;
                throw expected;
            });
            throw new InvalidOperationException("RED: Expected native upload failure.");
        }
        catch (InvalidOperationException ex)
        {
            Require(ReferenceEquals(ex, expected),
                "A failed native anchor upload must preserve the original exception.");
        }

        gate.Dispatch(3.5, 1.0, active: true, playing: false, () => uploads++);
        gate.Dispatch(3.5, 1.0, active: true, playing: false, () => uploads++);
        Require(uploads == 2,
            "A failed paused anchor must retry once at the same time, then deduplicate.");

        gate.Invalidate();
        try
        {
            gate.Dispatch(0.0, 1.0, active: false, playing: false, () =>
            {
                uploads++;
                throw expected;
            });
        }
        catch (InvalidOperationException ex)
        {
            Require(ReferenceEquals(ex, expected),
                "A failed stopped anchor must preserve the original exception.");
        }
        gate.Dispatch(0.0, 1.0, active: false, playing: false, () => uploads++);
        gate.Dispatch(0.0, 1.0, active: false, playing: false, () => uploads++);
        Require(uploads == 4,
            "A failed stopped anchor must retry once at the same time, then deduplicate.");
    }

    private static void VerifyViewportWiring()
    {
        string preparation = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/Native/NativeLevelViewport.Preparation.cs"));
        string prepared = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/Native/NativeLevelViewport.cs"));
        string shutdown = File.ReadAllText(FindSourceFile(
            "src/ExtremeEditor.Wpf/Native/NativeLevelViewport.Shutdown.cs"));

        Require(preparation.Split("_playbackAnchorGate.Dispatch(", StringSplitOptions.None).Length == 3,
            "Both SetPlaybackState and ClearPlayback must use retry-safe anchor dispatch.");
        Require(preparation.Contains("UpdateDecorationPlayback(chartTime);", StringComparison.Ordinal) &&
                preparation.Contains("UpdateDecorationPlayback(double.NegativeInfinity);", StringComparison.Ordinal),
            "Decoration evaluation must remain active while the native anchor is deduplicated.");
        Require(preparation.Contains("_playbackAnchorGate.Invalidate();", StringComparison.Ordinal) &&
                prepared.Contains("_playbackAnchorGate.Invalidate();", StringComparison.Ordinal) &&
                shutdown.Contains("_playbackAnchorGate.Invalidate();", StringComparison.Ordinal),
            "Native scene/timeline replacement and shutdown must invalidate cached anchors.");
    }

    private static string FindSourceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"Unable to locate source file {relativePath}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("RED: " + message);
    }
}
