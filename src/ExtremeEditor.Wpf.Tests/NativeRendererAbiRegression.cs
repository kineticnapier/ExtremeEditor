using System.Reflection;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeRendererAbiRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type bridge = assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererNative")
            ?? throw new InvalidOperationException("NativeRendererNative is missing.");
        MethodInfo getApiVersion = bridge.GetMethod(
            "GetApiVersion",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NativeRendererNative.GetApiVersion is missing.");

        uint version = (uint)(getApiVersion.Invoke(null, null) ?? 0u);
        if (version != 10u)
            throw new InvalidOperationException($"Native renderer API version mismatch: {version}.");

        Type diagnostics = assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererDiagnostics")
            ?? throw new InvalidOperationException("NativeRendererDiagnostics is missing.");

        string[] requiredPhaseFields =
        [
            "UpdateMilliseconds",
            "DrawSetupMilliseconds",
            "FloorMilliseconds",
            "IconMilliseconds",
            "OverlayMilliseconds",
            "EndDrawMilliseconds",
            "PresentMilliseconds",
            "TrackTotalMilliseconds",
            "TrackClockMilliseconds",
            "TrackEvaluateMilliseconds",
            "TrackApplyMilliseconds",
            "TrackSpatialRemoveMilliseconds",
            "TrackSpatialInsertMilliseconds",
            "TrackTotalCount",
            "TrackActiveCount",
            "TrackAdmittedCount",
            "TrackFinishedCount",
            "TrackEventScanCount",
            "TrackMaxEventScanCount"
        ];

        string[] missing = requiredPhaseFields
            .Where(name => diagnostics.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is null)
            .ToArray();

        if (missing.Length != 0)
        {
            throw new InvalidOperationException(
                "RED: native renderer diagnostics must expose per-frame phase timings: " +
                string.Join(", ", missing));
        }
    }
}
