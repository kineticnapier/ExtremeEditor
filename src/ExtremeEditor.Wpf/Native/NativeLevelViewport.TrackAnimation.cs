namespace ExtremeEditor.Wpf.Native;

public sealed partial class NativeLevelViewport
{
    private NativeTrackAnimationSegment[] _trackAnimationTimeline = [];
    private NativeTrackAnimationTiming[] _trackAnimationTimings = [];

    private void UploadPendingTrackAnimationTimeline()
    {
        _session?.SetTrackAnimationTimeline(_trackAnimationTimeline, _trackAnimationTimings);
    }
}
