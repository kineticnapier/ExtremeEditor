using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ExtremeEditor.Audio;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed class TogetherTimelineSelectionChangedEventArgs(
    long startSample,
    long endSampleExclusive) : EventArgs
{
    public long StartSample { get; } = startSample;
    public long EndSampleExclusive { get; } = endSampleExclusive;
}

/// <summary>
/// Lightweight drawing surface for the Together music timeline. It creates no
/// per-sample WPF elements; one retained frame is painted for the visible
/// sample window only.
/// </summary>
public sealed class TogetherMusicTimelineControl : FrameworkElement
{
    private static readonly Brush BackgroundBrush =
        new SolidColorBrush(Color.FromRgb(28, 32, 39));
    private static readonly Brush WaveformBrush =
        new SolidColorBrush(Color.FromRgb(84, 187, 226));
    private static readonly Pen SubdivisionPen =
        new(new SolidColorBrush(Color.FromArgb(95, 190, 198, 210)), 1);
    private static readonly Pen WholeBeatPen =
        new(new SolidColorBrush(Color.FromArgb(210, 238, 204, 92)), 1.5);
    private static readonly Brush SelectionBrush =
        new SolidColorBrush(Color.FromArgb(75, 90, 170, 255));
    private static readonly Pen SelectionPen =
        new(new SolidColorBrush(Color.FromRgb(90, 170, 255)), 1.5);
    private static readonly Brush SegmentBrush =
        new SolidColorBrush(Color.FromArgb(105, 116, 92, 196));
    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Yu Gothic UI"), FontStyles.Normal,
        FontWeights.SemiBold, FontStretches.Normal);

    private AudioWaveformData? _waveform;
    private MusicTimingMap? _timingMap;
    private TogetherMusicTimelineViewport? _viewport;
    private IReadOnlyList<SegmentTimePartition> _segments =
        Array.Empty<SegmentTimePartition>();
    private MusicBeatSubdivision _subdivision = MusicBeatSubdivision.Quarter;
    private TogetherMusicTimelineFrame? _frame;
    private long? _selectionAnchor;
    private long? _selectionCurrent;

    public event EventHandler<TogetherTimelineSelectionChangedEventArgs>?
        SelectionChanged;
    public event EventHandler? ViewportChanged;

    internal long TotalSamples => _viewport?.TotalSamples ?? 0;
    internal long VisibleStartSample => _viewport?.VisibleStartSample ?? 0;
    internal long VisibleEndSampleExclusive =>
        _viewport?.VisibleEndSampleExclusive ?? 0;
    internal double SamplesPerPixel => _viewport?.SamplesPerPixel ?? 1;

    internal void Load(
        AudioWaveformData waveform,
        MusicTimingMap timingMap,
        IReadOnlyList<SegmentTimePartition>? segments = null)
    {
        _waveform = waveform ?? throw new ArgumentNullException(nameof(waveform));
        _timingMap = timingMap ?? throw new ArgumentNullException(nameof(timingMap));
        _segments = segments?.ToArray() ?? Array.Empty<SegmentTimePartition>();
        _viewport = new TogetherMusicTimelineViewport(waveform, timingMap);
        double width = Math.Max(1, ActualWidth);
        _viewport.Configure(
            width,
            0,
            Math.Max(1d / 64d, waveform.TotalSamples / width));
        _selectionAnchor = null;
        _selectionCurrent = null;
        RefreshFrame();
        RaiseViewportChanged();
    }

    internal void Clear()
    {
        _waveform = null;
        _timingMap = null;
        _viewport = null;
        _frame = null;
        _segments = Array.Empty<SegmentTimePartition>();
        _selectionAnchor = null;
        _selectionCurrent = null;
        InvalidateVisual();
        RaiseViewportChanged();
    }

    internal void SetTimingMap(MusicTimingMap timingMap)
    {
        if (_waveform is null)
            return;
        long previousStart = VisibleStartSample;
        double previousScale = SamplesPerPixel;
        _timingMap = timingMap ?? throw new ArgumentNullException(nameof(timingMap));
        _viewport = new TogetherMusicTimelineViewport(_waveform, timingMap);
        _viewport.Configure(Math.Max(1, ActualWidth), previousStart, previousScale);
        RefreshFrame();
        RaiseViewportChanged();
    }

    internal void SetSegments(IReadOnlyList<SegmentTimePartition> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _segments = segments.ToArray();
        RefreshFrame();
    }

    internal void SetSubdivision(MusicBeatSubdivision subdivision)
    {
        _subdivision = subdivision;
        RefreshFrame();
    }

    internal void SetZoomLevel(double level)
    {
        if (_viewport is null)
            return;
        double normalized = Math.Clamp(level, 0, 1);
        double fullScale = Math.Max(1d / 64d,
            _viewport.TotalSamples / Math.Max(1, ActualWidth));
        double target = Math.Max(1d / 64d,
            fullScale / Math.Pow(2, normalized * 16));
        double factor = target / _viewport.SamplesPerPixel;
        _viewport.ZoomAt(Math.Max(1, ActualWidth) / 2, factor);
        RefreshFrame();
        RaiseViewportChanged();
    }

    internal void SetScrollSample(long startSample)
    {
        if (_viewport is null)
            return;
        _viewport.Configure(
            Math.Max(1, ActualWidth), startSample, _viewport.SamplesPerPixel);
        RefreshFrame();
        RaiseViewportChanged();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_viewport is null || ActualWidth <= 0)
            return;
        _viewport.Configure(
            ActualWidth,
            _viewport.VisibleStartSample,
            _viewport.SamplesPerPixel);
        RefreshFrame();
        RaiseViewportChanged();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_viewport is null)
            return;
        Focus();
        CaptureMouse();
        long sample = _viewport.SnapAtPixel(
            e.GetPosition(this).X, _subdivision).AudioSample;
        _selectionAnchor = sample;
        _selectionCurrent = sample;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_viewport is null || _selectionAnchor is null ||
            e.LeftButton != MouseButtonState.Pressed)
            return;
        _selectionCurrent = _viewport.SnapAtPixel(
            e.GetPosition(this).X, _subdivision).AudioSample;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_viewport is null || _selectionAnchor is not long anchor)
            return;
        _selectionCurrent = _viewport.SnapAtPixel(
            e.GetPosition(this).X, _subdivision).AudioSample;
        ReleaseMouseCapture();
        if (_selectionCurrent is long current && current != anchor)
        {
            long start = Math.Min(anchor, current);
            long end = Math.Max(anchor, current);
            SelectionChanged?.Invoke(this,
                new TogetherTimelineSelectionChangedEventArgs(start, end));
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_viewport is null)
            return;
        _viewport.ZoomAt(e.GetPosition(this).X,
            e.Delta > 0 ? 0.8 : 1.25);
        RefreshFrame();
        RaiseViewportChanged();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null,
            new Rect(0, 0, ActualWidth, ActualHeight));
        if (_frame is null || ActualHeight <= 0)
            return;

        const double segmentHeight = 24;
        foreach (TogetherTimelineSegmentBand band in _frame.SegmentBands)
        {
            var rect = new Rect(band.X, 0, band.Width, segmentHeight);
            drawingContext.DrawRectangle(SegmentBrush, null, rect);
            string label = string.IsNullOrWhiteSpace(band.OwnerId)
                ? band.SegmentId
                : $"{band.SegmentId} · {band.OwnerId}";
            drawingContext.DrawText(new FormattedText(
                    label,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    11,
                    Brushes.White,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip),
                new Point(band.X + 4, 4));
        }

        foreach (TogetherTimelineGridMark mark in _frame.GridMarks)
        {
            Pen pen = mark.Line.IsWholeBeat ? WholeBeatPen : SubdivisionPen;
            drawingContext.DrawLine(pen,
                new Point(mark.X, 0), new Point(mark.X, ActualHeight));
        }

        double waveformTop = segmentHeight + 4;
        double center = waveformTop + (ActualHeight - waveformTop) / 2;
        double amplitude = Math.Max(1, (ActualHeight - waveformTop) / 2 - 4);
        foreach (TogetherTimelineWaveformColumn column in _frame.WaveformColumns)
        {
            double top = center - column.Maximum * amplitude;
            double bottom = center - column.Minimum * amplitude;
            drawingContext.DrawRectangle(WaveformBrush, null,
                new Rect(
                    column.X,
                    top,
                    Math.Max(0.5, column.Width),
                    Math.Max(0.5, bottom - top)));
        }

        if (_selectionAnchor is long anchor &&
            _selectionCurrent is long current && _viewport is not null)
        {
            double x1 = (anchor - _viewport.VisibleStartSample) /
                _viewport.SamplesPerPixel;
            double x2 = (current - _viewport.VisibleStartSample) /
                _viewport.SamplesPerPixel;
            var selection = new Rect(
                Math.Min(x1, x2), 0,
                Math.Abs(x2 - x1), ActualHeight);
            drawingContext.DrawRectangle(
                SelectionBrush, SelectionPen, selection);
        }
    }

    private void RefreshFrame()
    {
        if (_viewport is null)
        {
            _frame = null;
            InvalidateVisual();
            return;
        }

        MusicBeatSubdivision[] fallbacks =
        [
            MusicBeatSubdivision.Sixteenth,
            MusicBeatSubdivision.Eighth,
            MusicBeatSubdivision.Quarter,
            MusicBeatSubdivision.Third,
            MusicBeatSubdivision.Half,
            MusicBeatSubdivision.One
        ];
        int requested = (int)_subdivision;
        foreach (MusicBeatSubdivision candidate in fallbacks
                     .Where(item => (int)item <= requested))
        {
            try
            {
                _frame = _viewport.Project(candidate, _segments, 8_192);
                InvalidateVisual();
                return;
            }
            catch (InvalidOperationException)
            {
                // Too many visible lines at this zoom. Draw the next coarser
                // grid while keeping selection snapping at the chosen value.
            }
        }

        _frame = _viewport.Project(
            MusicBeatSubdivision.One, _segments, 100_000);
        InvalidateVisual();
    }

    private void RaiseViewportChanged() =>
        ViewportChanged?.Invoke(this, EventArgs.Empty);
}
