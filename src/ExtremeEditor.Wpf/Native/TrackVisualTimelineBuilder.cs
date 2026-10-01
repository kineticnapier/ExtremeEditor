using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class TrackVisualTimelineBuilder
{
    internal static NativeTrackVisualEvent[] Build(LevelDocument level, TimingMap? timingMap = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (level.FloorCount == 0)
            return [];

        TrackVisualSourceData metadata = TrackVisualMetadataCache.Get(level);
        Dictionary<int, TrackVisualSourceEvent> sources = TrackVisualResolver
            .BuildLiveEvents(level, metadata.Events)
            .Where(static item => item.EventType == "RecolorTrack" && item.Active)
            .GroupBy(static item => item.SourceIndex)
            .ToDictionary(static group => group.Key, static group => group.Last());
        if (sources.Count == 0)
            return [];

        double pitch = Math.Max(0.000001, level.PitchPercent * 0.01);
        var result = new List<NativeTrackVisualEvent>();
        TimingMap timing = timingMap ?? TimingMapBuilder.Build(level);
        VfxTimeline timeline = VfxTimelineBuilder.Build(
            level,
            timing,
            static action => action.EventType == "RecolorTrack");
        foreach (VfxOccurrence occurrence in timeline.Occurrences)
        {
            if (!occurrence.Active || occurrence.EventType != "RecolorTrack" ||
                !sources.TryGetValue(occurrence.SourceIndex, out TrackVisualSourceEvent? source))
                continue;

            TrackVisualSourceEvent occurrenceSource = source with { Floor = occurrence.Floor };
            ResolvedTrackVisualRange range = TrackVisualResolver.ResolveRecolorRange(occurrenceSource, level.FloorCount);
            if (range.End < range.Start)
                continue;
            TrackVisualStyle style = TrackVisualResolver.ApplyStyle(metadata.InitialStyle, occurrenceSource, range.Start);
            NativeTrackVisual packed = TrackVisualResolver.Pack(style, pitch);
            result.Add(new NativeTrackVisualEvent
            {
                StartTime = occurrence.StartTime,
                TransitionDuration = Math.Max(0.0, occurrence.DurationSeconds ?? 0.0),
                StartFloor = range.Start,
                EndFloor = range.End,
                GapLength = checked((uint)Math.Max(0, range.Step - 1)),
                PrimaryColor = packed.PrimaryColor,
                SecondaryColor = packed.SecondaryColor,
                VisualFlags = packed.Flags,
                AnimDuration = packed.AnimDuration,
                GlowIntensity = packed.GlowIntensity,
                PulseLength = packed.PulseLength,
                Ease = ParseEase(occurrence.Ease),
                SourceIndex = occurrence.SourceIndex
            });
        }

        return result
            .OrderBy(static item => item.StartTime)
            .ThenBy(static item => item.SourceIndex)
            .ToArray();
    }

    private static uint ParseEase(string? ease) => ease switch
    {
        "InSine" => NativeCameraEvent.EaseInSine,
        "OutSine" => NativeCameraEvent.EaseOutSine,
        "InOutSine" => NativeCameraEvent.EaseInOutSine,
        "InQuad" => NativeCameraEvent.EaseInQuad,
        "OutQuad" => NativeCameraEvent.EaseOutQuad,
        "InOutQuad" => NativeCameraEvent.EaseInOutQuad,
        "InCubic" => NativeCameraEvent.EaseInCubic,
        "OutCubic" => NativeCameraEvent.EaseOutCubic,
        "InOutCubic" => NativeCameraEvent.EaseInOutCubic,
        _ => NativeCameraEvent.EaseLinear
    };
}
