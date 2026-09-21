using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct NativeLevelUploadMetrics(
    TimeSpan SnapshotBuild,
    TimeSpan NativeUpload,
    TimeSpan SnapshotFloorGeometry,
    TimeSpan SnapshotIcons,
    TimeSpan SnapshotFinalize,
    int GeometryCount,
    int IconAssetCount,
    int ActionFloorCount);

internal readonly record struct NativePlaybackTimelineUploadMetrics(
    TimeSpan TimelineBuild,
    TimeSpan NativeUpload);

internal sealed class NativeFloorSelectionRequestedEventArgs : RoutedEventArgs
{
    internal NativeFloorSelectionRequestedEventArgs(RoutedEvent routedEvent, int floor, ModifierKeys modifiers)
        : base(routedEvent)
    {
        Floor = floor;
        Modifiers = modifiers;
    }

    internal int Floor { get; }
    internal ModifierKeys Modifiers { get; }
}

internal sealed class NativeEditorActionRequestedEventArgs : RoutedEventArgs
{
    internal NativeEditorActionRequestedEventArgs(RoutedEvent routedEvent, NativeEditorActionRequest request)
        : base(routedEvent)
    {
        Request = request;
    }

    internal NativeEditorActionRequest Request { get; }
}

public sealed class NativeLevelViewport : HwndHost
{
    internal static readonly RoutedEvent FloorSelectionRequestedEvent = EventManager.RegisterRoutedEvent(
        "FloorSelectionRequested",
        RoutingStrategy.Bubble,
        typeof(EventHandler<NativeFloorSelectionRequestedEventArgs>),
        typeof(NativeLevelViewport));

    internal static readonly RoutedEvent EditorActionRequestedEvent = EventManager.RegisterRoutedEvent(
        "EditorActionRequested",
        RoutingStrategy.Bubble,
        typeof(EventHandler<NativeEditorActionRequestedEventArgs>),
        typeof(NativeLevelViewport));

    private NativeRendererSession? _session;
    private LevelDocument? _level;
    private NativeLevelSnapshot? _snapshot;
    private NativePlaybackTiming[] _playbackTimeline = [];
    private int[] _selectedFloors = [];
    private int _primarySelection = -1;
    private bool _frameAllPending;
    private bool _followPlayer = true;

    public NativeLevelViewport()
    {
        Focusable = false;
        SizeChanged += (_, _) => ResizeNativeChild();
    }

    // Retained for compatibility with the earlier single-selection bridge. New
    // editor code uses the routed event above so modifier keys survive HWND input.
    public event Action<int>? SelectedFloorChanged;
    public event Action<bool>? FollowPlayerChanged;

    internal NativeLevelUploadMetrics LastLevelUploadMetrics { get; private set; }
    internal NativePlaybackTimelineUploadMetrics LastPlaybackTimelineUploadMetrics { get; private set; }

    public bool FollowPlayer
    {
        get => _followPlayer;
        set
        {
            if (_followPlayer == value)
                return;

            _followPlayer = value;
            _session?.SetFollowPlayer(value);
        }
    }

    public void SetLevel(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        bool changedDocument = !ReferenceEquals(_level, level);
        _level = level;
        _snapshot = null;
        if (changedDocument)
        {
            _selectedFloors = [];
            _primarySelection = -1;
        }
        LastLevelUploadMetrics = UploadPendingLevel();
    }

    public void SetSelection(IEnumerable<int> floors, int primaryFloor)
    {
        if (_level is null)
        {
            _selectedFloors = [];
            _primarySelection = -1;
            _session?.SetSelection(_selectedFloors, _primarySelection);
            return;
        }

        _selectedFloors = floors
            .Where(floor => (uint)floor < (uint)_level.FloorCount)
            .Distinct()
            .OrderBy(floor => floor)
            .ToArray();
        _primarySelection = _selectedFloors.Contains(primaryFloor)
            ? primaryFloor
            : _selectedFloors.Length > 0 ? _selectedFloors[^1] : -1;
        _session?.SetSelection(_selectedFloors, _primarySelection);
    }

    public void SetPlaybackTimeline(TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(timingMap);

        var watch = Stopwatch.StartNew();
        _playbackTimeline = NativePlaybackTimelineBuilder.Build(timingMap);
        watch.Stop();
        TimeSpan buildTime = watch.Elapsed;

        watch.Restart();
        UploadPendingPlaybackTimeline();
        watch.Stop();
        LastPlaybackTimelineUploadMetrics = new NativePlaybackTimelineUploadMetrics(
            buildTime,
            watch.Elapsed);
    }

    public void SetPlaybackState(double chartTime, double chartRate, bool active, bool playing)
    {
        _session?.SetPlaybackAnchor(chartTime, chartRate, active, playing);
    }

    public void ClearPlayback()
    {
        _session?.SetPlaybackAnchor(0.0, 1.0, active: false, playing: false);
    }

    internal bool TryGetDiagnostics(out NativeRendererDiagnostics diagnostics)
    {
        if (_session is not null)
            return _session.TryGetDiagnostics(out diagnostics);

        diagnostics = default;
        return false;
    }

    public void FrameAll()
    {
        if (_session is null)
        {
            _frameAllPending = true;
            return;
        }

        _session.FrameAll();
        _frameAllPending = false;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        uint width = ToPixelExtent(ActualWidth);
        uint height = ToPixelExtent(ActualHeight);
        _session = NativeRendererSession.Create(hwndParent.Handle, width, height);
        _session.SelectionChanged += NativeSelectionChanged;
        _session.FollowPlayerChanged += NativeFollowPlayerChanged;
        _session.EditorActionRequested += NativeEditorActionRequested;
        _session.SetFollowPlayer(_followPlayer);
        LastLevelUploadMetrics = UploadPendingLevel();

        var watch = Stopwatch.StartNew();
        UploadPendingPlaybackTimeline();
        watch.Stop();
        LastPlaybackTimelineUploadMetrics = LastPlaybackTimelineUploadMetrics with
        {
            NativeUpload = watch.Elapsed
        };

        if (_frameAllPending)
        {
            _session.FrameAll();
            _frameAllPending = false;
        }
        return new HandleRef(this, _session.ChildHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_session is not null)
        {
            _session.SelectionChanged -= NativeSelectionChanged;
            _session.FollowPlayerChanged -= NativeFollowPlayerChanged;
            _session.EditorActionRequested -= NativeEditorActionRequested;
        }
        _session?.Dispose();
        _session = null;
    }

    private void NativeSelectionChanged(int floor, uint nativeModifiers)
    {
        // The native child HWND owns mouse input. Carry the modifier snapshot from
        // WM_LBUTTONDOWN itself rather than asking WPF for state after the callback.
        ModifierKeys modifiers = ToModifierKeys(nativeModifiers);
        void RaiseSelection()
        {
            RaiseEvent(new NativeFloorSelectionRequestedEventArgs(
                FloorSelectionRequestedEvent,
                floor,
                modifiers));
        }

        if (Dispatcher.CheckAccess())
            RaiseSelection();
        else
            Dispatcher.BeginInvoke((Action)RaiseSelection);
    }

    private void NativeEditorActionRequested(NativeEditorActionRequest request)
    {
        void RaiseAction() => RaiseEvent(new NativeEditorActionRequestedEventArgs(EditorActionRequestedEvent, request));

        if (Dispatcher.CheckAccess())
            RaiseAction();
        else
            Dispatcher.BeginInvoke((Action)RaiseAction);
    }

    private void NativeFollowPlayerChanged(bool enabled)
    {
        if (_followPlayer == enabled)
            return;

        _followPlayer = enabled;
        FollowPlayerChanged?.Invoke(enabled);
    }

    private NativeLevelUploadMetrics UploadPendingLevel()
    {
        if (_session is null || _level is null)
            return default;

        TimeSpan snapshotBuild = TimeSpan.Zero;
        NativeLevelSnapshotBuildMetrics buildMetrics = default;
        if (_snapshot is null)
        {
            var snapshotWatch = Stopwatch.StartNew();
            NativeLevelSnapshotBuildResult result = FlatNativeLevelSnapshotBuilder.BuildProfiled(_level);
            _snapshot = result.Snapshot;
            buildMetrics = result.Metrics;
            snapshotWatch.Stop();
            snapshotBuild = snapshotWatch.Elapsed;
        }

        var uploadWatch = Stopwatch.StartNew();
        _session.SetLevel(_snapshot);
        _session.SetSelection(_selectedFloors, _primarySelection);
        uploadWatch.Stop();

        return new NativeLevelUploadMetrics(
            snapshotBuild,
            uploadWatch.Elapsed,
            buildMetrics.FloorGeometry,
            buildMetrics.Icons,
            buildMetrics.Finalize,
            buildMetrics.GeometryCount,
            buildMetrics.IconAssetCount,
            buildMetrics.ActionFloorCount);
    }

    private void UploadPendingPlaybackTimeline()
    {
        if (_session is null)
            return;

        _session.SetPlaybackTimeline(_playbackTimeline);
    }

    private void ResizeNativeChild()
    {
        _session?.Resize(ToPixelExtent(ActualWidth), ToPixelExtent(ActualHeight));
    }

    private static ModifierKeys ToModifierKeys(uint nativeModifiers)
    {
        ModifierKeys result = ModifierKeys.None;
        if ((nativeModifiers & NativeRendererNative.InputModifierShift) != 0)
            result |= ModifierKeys.Shift;
        if ((nativeModifiers & NativeRendererNative.InputModifierControl) != 0)
            result |= ModifierKeys.Control;
        if ((nativeModifiers & NativeRendererNative.InputModifierAlt) != 0)
            result |= ModifierKeys.Alt;
        if ((nativeModifiers & NativeRendererNative.InputModifierWindows) != 0)
            result |= ModifierKeys.Windows;
        return result;
    }

    private static uint ToPixelExtent(double value)
    {
        if (!double.IsFinite(value) || value <= 0d)
            return 1u;

        return checked((uint)Math.Clamp(Math.Ceiling(value), 1d, uint.MaxValue));
    }
}