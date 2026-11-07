namespace ExtremeEditor.Wpf.Native;

/// <summary>
/// Converts the camera timeline's view-size multiplier (zoom / 100) for a
/// render target's pixel height.
///
/// Stock ADOFAI (scrCamera.UpdateSize) uses an orthographic size of
/// 5 * userSizeMultiplier * zoomSize, so one world unit covers
/// height / (10 * zoomSize) pixels. The native renderer draws
/// 56 / multiplier pixels per unit, which matches stock at a 560 px tall
/// target; scaling the multiplier by 560 / height keeps the board
/// proportional to the screen height for every target, realtime or headless.
/// </summary>
internal static class NativeCameraZoomScale
{
    internal const float ReferenceHeight = 560f;

    internal static float ForHeight(uint viewportHeight) =>
        ReferenceHeight / Math.Max(1f, viewportHeight);

    /// <summary>
    /// Returns the timeline to upload for <paramref name="viewportHeight"/>:
    /// the source itself at the reference height, otherwise a copy whose zoom
    /// channel is scaled. The source is never modified.
    /// </summary>
    internal static NativeCameraEvent[] ForViewportHeight(NativeCameraEvent[] timeline, uint viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        float zoomScale = ForHeight(viewportHeight);
        if (timeline.Length == 0 || Math.Abs(zoomScale - 1f) < 0.000001f)
            return timeline;

        var adjusted = new NativeCameraEvent[timeline.Length];
        for (int i = 0; i < timeline.Length; i++)
        {
            adjusted[i] = timeline[i];
            adjusted[i].StartZoom *= zoomScale;
            adjusted[i].TargetZoom *= zoomScale;
        }
        return adjusted;
    }
}
