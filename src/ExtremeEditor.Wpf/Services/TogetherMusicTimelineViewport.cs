using ExtremeEditor.Audio;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal sealed record TogetherTimelineGridMark(
    double X,
    MusicTimingGridLine Line);

internal sealed record TogetherTimelineWaveformColumn(
    double X,
    double Width,
    float Minimum,
    float Maximum);

internal sealed record TogetherTimelineSegmentBand(
    double X,
    double Width,
    string SegmentId,
    string? OwnerId);

internal sealed record TogetherMusicTimelineFrame(
    long VisibleStartSample,
    long VisibleEndSampleExclusive,
    IReadOnlyList<TogetherTimelineGridMark> GridMarks,
    IReadOnlyList<TogetherTimelineWaveformColumn> WaveformColumns,
    IReadOnlyList<TogetherTimelineSegmentBand> SegmentBands);

/// <summary>
/// RED contract for sample/pixel projection. Rendering and pointer handling
/// consume this model without generating a whole-song grid.
/// </summary>
internal sealed class TogetherMusicTimelineViewport
{
    internal TogetherMusicTimelineViewport(
        AudioWaveformData waveform,
        MusicTimingMap timingMap) =>
        throw new NotImplementedException();

    internal double Width => throw new NotImplementedException();
    internal double SamplesPerPixel => throw new NotImplementedException();
    internal long VisibleStartSample => throw new NotImplementedException();
    internal long VisibleEndSampleExclusive => throw new NotImplementedException();

    internal void Configure(
        double width,
        long visibleStartSample,
        double samplesPerPixel) =>
        throw new NotImplementedException();

    internal void ZoomAt(double x, double factor) =>
        throw new NotImplementedException();

    internal void ScrollByPixels(double deltaPixels) =>
        throw new NotImplementedException();

    internal MusicTimingSnap SnapAtPixel(
        double x,
        MusicBeatSubdivision subdivision) =>
        throw new NotImplementedException();

    internal TogetherMusicTimelineFrame Project(
        MusicBeatSubdivision subdivision,
        IReadOnlyList<SegmentTimePartition> segments,
        int maximumGridLineCount = 4_096) =>
        throw new NotImplementedException();
}
