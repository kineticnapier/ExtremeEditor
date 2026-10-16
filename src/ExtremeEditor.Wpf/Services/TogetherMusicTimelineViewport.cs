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
    private readonly AudioWaveformData _waveform;
    private readonly MusicTimingMap _timingMap;
    private double _width;
    private double _samplesPerPixel;
    private long _visibleStartSample;

    internal TogetherMusicTimelineViewport(
        AudioWaveformData waveform,
        MusicTimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        ArgumentNullException.ThrowIfNull(timingMap);
        if (waveform.SampleRate <= 0 || waveform.TotalSamples <= 0 ||
            waveform.Peaks is null || waveform.Peaks.Count == 0)
            throw new ArgumentException(
                "The music timeline requires a valid waveform.",
                nameof(waveform));
        if (timingMap.SampleRate != waveform.SampleRate)
            throw new ArgumentException(
                "The music grid and waveform must use the same sample rate.",
                nameof(timingMap));

        long previousEnd = 0;
        foreach (AudioWaveformPeak? peak in waveform.Peaks)
        {
            if (peak is null ||
                peak.StartSample != previousEnd ||
                peak.EndSampleExclusive <= peak.StartSample ||
                peak.EndSampleExclusive > waveform.TotalSamples ||
                !float.IsFinite(peak.Minimum) ||
                !float.IsFinite(peak.Maximum) ||
                peak.Minimum > peak.Maximum)
                throw new ArgumentException(
                    "The waveform peak ranges are invalid or noncontiguous.",
                    nameof(waveform));
            previousEnd = peak.EndSampleExclusive;
        }
        if (previousEnd != waveform.TotalSamples)
            throw new ArgumentException(
                "The waveform peaks do not cover the decoded audio duration.",
                nameof(waveform));

        _waveform = waveform;
        _timingMap = timingMap;
        _width = 1;
        _samplesPerPixel = waveform.TotalSamples;
    }

    internal double Width => _width;
    internal double SamplesPerPixel => _samplesPerPixel;
    internal long VisibleStartSample => _visibleStartSample;
    internal long VisibleEndSampleExclusive => Math.Min(
        _waveform.TotalSamples,
        checked(_visibleStartSample + VisibleSpanSamples));
    internal long TotalSamples => _waveform.TotalSamples;

    internal void Configure(
        double width,
        long visibleStartSample,
        double samplesPerPixel)
    {
        if (!double.IsFinite(width) || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(samplesPerPixel) || samplesPerPixel <= 0)
            throw new ArgumentOutOfRangeException(nameof(samplesPerPixel));

        _width = width;
        _samplesPerPixel = Math.Clamp(
            samplesPerPixel,
            1d / 64d,
            Math.Max(1d / 64d, _waveform.TotalSamples));
        long span = VisibleSpanSamples;
        long maximumStart = Math.Max(0, _waveform.TotalSamples - span);
        _visibleStartSample = Math.Clamp(
            visibleStartSample, 0, maximumStart);
    }

    internal void ZoomAt(double x, double factor)
    {
        if (!double.IsFinite(x))
            throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor));

        double clampedX = Math.Clamp(x, 0, _width);
        decimal anchor = _visibleStartSample +
            (decimal)clampedX * (decimal)_samplesPerPixel;
        double newScale = Math.Clamp(
            _samplesPerPixel * factor,
            1d / 64d,
            Math.Max(1d / 64d, _waveform.TotalSamples));
        long newStart = CheckedRound(anchor -
            (decimal)clampedX * (decimal)newScale);
        Configure(_width, newStart, newScale);
    }

    internal void ScrollByPixels(double deltaPixels)
    {
        if (!double.IsFinite(deltaPixels))
            throw new ArgumentOutOfRangeException(nameof(deltaPixels));
        long delta = CheckedRound(
            (decimal)deltaPixels * (decimal)_samplesPerPixel);
        long requested = AddSaturating(_visibleStartSample, delta);
        Configure(_width, requested, _samplesPerPixel);
    }

    internal MusicTimingSnap SnapAtPixel(
        double x,
        MusicBeatSubdivision subdivision)
    {
        if (!double.IsFinite(x))
            throw new ArgumentOutOfRangeException(nameof(x));
        double clamped = Math.Clamp(x, 0, _width);
        long requested = AddSaturating(
            _visibleStartSample,
            CheckedRound((decimal)clamped *
                (decimal)_samplesPerPixel));
        requested = Math.Clamp(requested, 0, _waveform.TotalSamples);
        MusicTimingSnap snap = _timingMap.Snap(requested, subdivision);
        if (snap.AudioSample < 0)
            return snap with { AudioSample = 0 };
        if (snap.AudioSample > _waveform.TotalSamples)
            return snap with { AudioSample = _waveform.TotalSamples };
        return snap;
    }

    internal TogetherMusicTimelineFrame Project(
        MusicBeatSubdivision subdivision,
        IReadOnlyList<SegmentTimePartition> segments,
        int maximumGridLineCount = 4_096)
    {
        ArgumentNullException.ThrowIfNull(segments);
        long visibleEnd = VisibleEndSampleExclusive;
        IReadOnlyList<MusicTimingGridLine> lines =
            _timingMap.GetVisibleLines(
                _visibleStartSample,
                visibleEnd,
                subdivision,
                maximumGridLineCount);
        TogetherTimelineGridMark[] marks = lines.Select(line =>
            new TogetherTimelineGridMark(ToX(line.AudioSample), line)).ToArray();

        TogetherTimelineWaveformColumn[] waveform = _waveform.Peaks
            .Where(peak => peak.StartSample < visibleEnd &&
                           peak.EndSampleExclusive > _visibleStartSample)
            .Select(peak =>
            {
                double x1 = Math.Clamp(ToX(peak.StartSample), 0, _width);
                double x2 = Math.Clamp(ToX(peak.EndSampleExclusive), 0, _width);
                return new TogetherTimelineWaveformColumn(
                    x1, Math.Max(0.5, x2 - x1),
                    peak.Minimum, peak.Maximum);
            }).ToArray();

        var bands = new List<TogetherTimelineSegmentBand>();
        foreach (SegmentTimePartition? segment in segments)
        {
            if (segment is null ||
                string.IsNullOrWhiteSpace(segment.SegmentId) ||
                segment.StartSample < 0 ||
                segment.EndSampleExclusive <= segment.StartSample)
                throw new ArgumentException(
                    "The timeline contains an invalid Segment band.",
                    nameof(segments));
            if (segment.StartSample >= visibleEnd ||
                segment.EndSampleExclusive <= _visibleStartSample)
                continue;

            double x1 = Math.Clamp(ToX(segment.StartSample), 0, _width);
            double x2 = Math.Clamp(ToX(segment.EndSampleExclusive), 0, _width);
            bands.Add(new TogetherTimelineSegmentBand(
                x1,
                Math.Max(0.5, x2 - x1),
                segment.SegmentId,
                segment.OwnerId));
        }

        return new TogetherMusicTimelineFrame(
            _visibleStartSample,
            visibleEnd,
            Array.AsReadOnly(marks),
            Array.AsReadOnly(waveform),
            Array.AsReadOnly(bands.ToArray()));
    }

    private long VisibleSpanSamples => Math.Max(1,
        CheckedCeiling((decimal)_width * (decimal)_samplesPerPixel));

    private double ToX(long sample) =>
        ((double)sample - _visibleStartSample) / _samplesPerPixel;

    private static long CheckedRound(decimal value)
    {
        decimal rounded = decimal.Round(
            value, 0, MidpointRounding.AwayFromZero);
        if (rounded < long.MinValue || rounded > long.MaxValue)
            throw new OverflowException("Timeline sample conversion overflowed.");
        return (long)rounded;
    }

    private static long CheckedCeiling(decimal value)
    {
        decimal ceiling = decimal.Ceiling(value);
        if (ceiling > long.MaxValue)
            return long.MaxValue;
        return (long)ceiling;
    }

    private static long AddSaturating(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
            return long.MaxValue;
        if (right < 0 && left < long.MinValue - right)
            return long.MinValue;
        return left + right;
    }
}
