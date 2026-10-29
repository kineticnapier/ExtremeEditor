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

public sealed partial class NativeLevelViewport : HwndHost
{
    private const float PlaybackReferenceHeight = 560f;

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
    private readonly NativePlaybackAnchorDispatchGate _playbackAnchorGate = new();
    private LevelDocument? _level;
    private NativeLevelSnapshot? _snapshot;
    private NativePlaybackTiming[] _playbackTimeline = [];
    private NativeCameraEvent[] _cameraTimeline = [];
    // Camera zoom conversion depends on viewport height, never width.
    private NativeRendererSession? _cameraUploadSession;
    private NativeCameraEvent[]? _cameraUploadedSource;
    private uint _cameraUploadedHeight;
    private NativeFlashEvent[] _flashTimeline = [];
    private NativeBloomEvent[] _bloomTimeline = [];
    private NativeScreenTileEvent[] _screenTileTimeline = [];
    private NativeFilterEvent[] _filterTimeline = [];
    private NativeBackgroundEvent[] _backgroundTimeline = [];
    private NativeTrackTransformEvent[] _trackTransformTimeline = [];
    private NativeTrackVisualEvent[] _trackVisualTimeline = [];
    private int[] _selectedFloors = [];
    private int _primarySelection = -1;
    private bool _frameAllPending;
    private bool _followPlayer = true;
    private bool _cleanView;
    private bool _showFloorNumbers;

    public NativeLevelViewport()
    {
        // Allow keyboard navigation to reach the HwndHost. Enter commits also
        // transfer focus explicitly to its native child HWND.
        Focusable = true;
        SizeChanged += (_, _) => ResizeNativeChild();
    }

    /// <summary>
    /// Return keyboard input to the actual native editor window after a WPF
    /// settings field commits. UIElement.Focus() alone is insufficient for a
    /// child HWND, especially when the WPF focus element is stale.
    /// </summary>
    public bool FocusEditorSurface()
    {
        nint childHwnd = _session?.ChildHwnd ?? nint.Zero;
        if (childHwnd == nint.Zero || !IsVisible || !IsEnabled)
            return false;

        SetFocus(childHwnd);
        return GetFocus() == childHwnd;
    }

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    public event Action<bool>? FollowPlayerChanged;

    internal NativeLevelUploadMetrics LastLevelUploadMetrics { get; private set; }
    internal NativePlaybackTimelineUploadMetrics LastPlaybackTimelineUploadMetrics { get; private set; }
    internal NativeLevelSnapshot? SnapshotForDiagnostics => _snapshot;

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

    public void SetCleanView(bool enabled)
    {
        _cleanView = enabled;
        _session?.SetCleanView(enabled);
    }

    public void SetShowFloorNumbers(bool enabled)
    {
        _showFloorNumbers = enabled;
        _session?.SetShowFloorNumbers(enabled);
    }

    public void SetLevel(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        NativeLevelUpdateDiagnostics.RecordFullLevelReplacementRequest();
        _playbackAnchorGate.Invalidate();
        bool changedDocument = !ReferenceEquals(_level, level);
        _level = level;
        _snapshot = null;
        if (changedDocument)
        {
            _selectedFloors = [];
            _primarySelection = -1;
            _cameraTimeline = [];
            _flashTimeline = [];
            _bloomTimeline = [];
            _screenTileTimeline = [];
            _filterTimeline = [];
            _backgroundTimeline = [];
            _trackTransformTimeline = [];
            _trackVisualTimeline = [];
            _trackAnimationTimeline = [];
            _trackAnimationTimings = [];
        }
        LastLevelUploadMetrics = UploadPendingLevel();
    }

    internal bool TryUpdateFloorIconsFrom(LevelDocument level, int startFloor)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (_snapshot is null ||
            !FloorIconResolver.TryResolveFrom(level, _snapshot, startFloor, out FloorIconSuffixResolution resolved))
            return false;

        if (_session is not null && !_session.UpdateFloorIcons(startFloor, resolved.States))
            return false;

        for (int i = 0; i < resolved.States.Length; i++)
            resolved.States[i].ApplyTo(ref _snapshot.Floors[startFloor + i]);
        Array.Copy(
            resolved.CcwBeforeFloorSuffix,
            0,
            _snapshot.CcwBeforeFloor,
            startFloor,
            resolved.CcwBeforeFloorSuffix.Length);
        _level = level;
        NativeLevelUpdateDiagnostics.RecordFloorIconUpdateRequest(startFloor);
        return true;
    }

    public void ReloadAssets()
    {
        _playbackAnchorGate.Invalidate();
        _snapshot = null;
        LastLevelUploadMetrics = UploadPendingLevel();
    }

    private void SyncBackwardHudButton()
    {
        int button = _level is not null && _selectedFloors.Length == 1
            ? global::ExtremeEditor.Wpf.AdoFaiPathDirection.GetBackwardHudButtonIndex(
                _level.Angles, _primarySelection)
            : -1;
        _session?.SetEditorHudDeleteButton(button);
    }

    public void SetSelection(IEnumerable<int> floors, int primaryFloor)
    {
        if (_level is null)
        {
            _selectedFloors = [];
            _primarySelection = -1;
            _session?.SetSelection(_selectedFloors, _primarySelection);
            SyncBackwardHudButton();
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
        SyncBackwardHudButton();
    }

    public void SetPlaybackTimeline(TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(timingMap);

        // Build every timeline before changing the active viewport state.
        // A builder exception must not leave playback timing from one chart
        // combined with camera, filter or decoration data from another.
        PreparedNativePlayback prepared;
        if (_level is LevelDocument level)
        {
            prepared = PreparePlayback(level, timingMap);
        }
        else
        {
            // Before a chart is loaded only the timing map is meaningful.
            var watch = Stopwatch.StartNew();
            NativePlaybackTiming[] playback = NativePlaybackTimelineBuilder.Build(timingMap);
            watch.Stop();
            prepared = new PreparedNativePlayback(
                playback, [], [], [], [], [], [], [], [], [], [],
                watch.Elapsed);
        }

        // The shared upload path restores all managed timelines and invalidates
        // the camera cache if a native upload fails partway through.
        SetPreparedPlayback(prepared);
    }

    public void SetPlaybackState(double chartTime, double chartRate, bool active, bool playing)
    {
        UpdateDecorationPlayback(chartTime);
        if (_session is NativeRendererSession session &&
            _playbackAnchorGate.ShouldSend(chartTime, chartRate, active, playing))
            session.SetPlaybackAnchor(chartTime, chartRate, active, playing);
    }

    public void ClearPlayback()
    {
        UpdateDecorationPlayback(double.NegativeInfinity);
        if (_session is NativeRendererSession session &&
            _playbackAnchorGate.ShouldSend(0.0, 1.0, active: false, playing: false))
            session.SetPlaybackAnchor(0.0, 1.0, active: false, playing: false);
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

    public void CenterFloor(int floor)
    {
        if (_snapshot is null || (uint)floor >= (uint)_snapshot.Floors.Length)
            return;
        NativeFloor target = _snapshot.Floors[floor];
        _session?.CenterAt(target.X, target.Y);
    }

    // HwndHost does not guarantee DestroyWindowCore after BuildWindowCore throws.
    // Release a partially created renderer, preserving the original exception.
    internal static T RunNativeHostInitialization<T>(Func<T> initialize, Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            return initialize();
        }
        catch
        {
            try
            {
                cleanup();
            }
            catch (Exception cleanupError)
            {
                Console.Error.WriteLine(
                    $"[native-host] cleanup after initialization failure: {cleanupError}");
            }
            throw;
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        return RunNativeHostInitialization(
            initialize: () =>
            {
                uint width = ToPixelExtent(ActualWidth);
                uint height = ToPixelExtent(ActualHeight);
                _session = NativeRendererSession.Create(hwndParent.Handle, width, height);
                _playbackAnchorGate.Invalidate();
                _session.SelectionChanged += NativeSelectionChanged;
                _session.FollowPlayerChanged += NativeFollowPlayerChanged;
                _session.EditorActionRequested += NativeEditorActionRequested;
                _session.SetFollowPlayer(_followPlayer);
                _session.SetCleanView(_cleanView);
                _session.SetShowFloorNumbers(_showFloorNumbers);
                LastLevelUploadMetrics = UploadPendingLevel();

                var watch = Stopwatch.StartNew();
                UploadPendingPlaybackTimeline();
                UploadPendingCameraTimeline();
                UploadPendingFlashTimeline();
                UploadPendingBloomTimeline();
                UploadPendingScreenTileTimeline();
                UploadPendingFilterTimeline();
                UploadPendingBackgroundTimeline();
                UploadPendingTrackTransformTimeline();
                UploadPendingTrackVisualTimeline();
                UploadPendingTrackAnimationTimeline();
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
            },
            cleanup: () => DestroyWindowCore(default));
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        NativeRendererSession? session = _session;
        _session = null;
        _playbackAnchorGate.Invalidate();
        _cameraUploadSession = null;
        _cameraUploadedSource = null;
        _cameraUploadedHeight = 0;

        if (session is null)
            return;

        session.SelectionChanged -= NativeSelectionChanged;
        session.FollowPlayerChanged -= NativeFollowPlayerChanged;
        session.EditorActionRequested -= NativeEditorActionRequested;
        session.Dispose();
    }

    private void NativeSelectionChanged(int floor, uint nativeModifiers)
    {
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
            // Do not publish an incomplete snapshot if decoration or background
            // preparation fails. A later retry must rebuild the entire scene.
            NativeLevelSnapshot pendingSnapshot = result.Snapshot;
            StaticDecorationSnapshotData decorationData = StaticDecorationSnapshotBuilder.Build(_level);
            pendingSnapshot.StaticDecorations = decorationData.Instances;
            pendingSnapshot.DecorationAssets = decorationData.Assets;
            pendingSnapshot.DecorationPlayback = decorationData.PlaybackRuntime;
            var (backgroundAssets, backgroundIds) = BackgroundAssetResolver.Build(_level);
            BackgroundSourceState initialBackground = BackgroundSourceReader.ReadInitial(_level);
            pendingSnapshot.BackgroundState = BackgroundNativeEncoding.Encode(initialBackground,
                backgroundIds.TryGetValue(initialBackground.Image, out uint imageId) ? imageId : 0u);
            pendingSnapshot.BackgroundAssets = backgroundAssets;
            _snapshot = pendingSnapshot;
            buildMetrics = result.Metrics;
            snapshotWatch.Stop();
            snapshotBuild = snapshotWatch.Elapsed;
        }

        var uploadWatch = Stopwatch.StartNew();
        _session.SetLevel(_snapshot);
        _session.SetSelection(_selectedFloors, _primarySelection);
        SyncBackwardHudButton();
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

    private void UpdateDecorationPlayback(double chartTime)
    {
        if (_snapshot?.DecorationPlayback is not NativeDecorationPlaybackRuntime playback ||
            !playback.Update(chartTime))
            return;
        _session?.SetStaticDecorations(_snapshot.StaticDecorations);
    }

    private void UploadPendingPlaybackTimeline()
    {
        if (_session is not null)
            _session.SetPlaybackTimeline(_playbackTimeline);
    }

    private void UploadPendingCameraTimeline()
    {
        if (_session is null)
            return;

        uint viewportHeight = ToPixelExtent(ActualHeight);
        if (ReferenceEquals(_cameraUploadSession, _session) &&
            ReferenceEquals(_cameraUploadedSource, _cameraTimeline) &&
            _cameraUploadedHeight == viewportHeight)
            return;

        float zoomScale = PlaybackReferenceHeight / Math.Max(1f, viewportHeight);
        if (_cameraTimeline.Length == 0 || Math.Abs(zoomScale - 1f) < 0.000001f)
        {
            _session.SetCameraTimeline(_cameraTimeline);
        }
        else
        {
            var adjusted = new NativeCameraEvent[_cameraTimeline.Length];
            for (int i = 0; i < _cameraTimeline.Length; i++)
            {
                adjusted[i] = _cameraTimeline[i];
                adjusted[i].StartZoom *= zoomScale;
                adjusted[i].TargetZoom *= zoomScale;
            }
            _session.SetCameraTimeline(adjusted);
        }

        // Update cache only after a successful native upload.
        _cameraUploadSession = _session;
        _cameraUploadedSource = _cameraTimeline;
        _cameraUploadedHeight = viewportHeight;
    }

    private void UploadPendingFlashTimeline()
    {
        if (_session is not null)
            _session.SetFlashTimeline(_flashTimeline);
    }

    private void UploadPendingBloomTimeline()
    {
        if (_session is not null)
            _session.SetBloomTimeline(_bloomTimeline);
    }

    private void UploadPendingScreenTileTimeline()
    {
        if (_session is not null)
            _session.SetScreenTileTimeline(_screenTileTimeline);
    }

    private void UploadPendingFilterTimeline()
    {
        if (_session is not null)
            _session.SetFilterTimeline(_filterTimeline);
    }

    private void UploadPendingBackgroundTimeline()
    {
        if (_session is not null)
            _session.SetBackgroundTimeline(_backgroundTimeline);
    }

    private void UploadPendingTrackTransformTimeline()
    {
        if (_session is not null)
            _session.SetTrackTransformTimeline(_trackTransformTimeline);
    }

    private void UploadPendingTrackVisualTimeline()
    {
        if (_session is not null)
            _session.SetTrackVisualTimeline(_trackVisualTimeline);
    }

    private void ResizeNativeChild()
    {
        _session?.Resize(ToPixelExtent(ActualWidth), ToPixelExtent(ActualHeight));
        UploadPendingCameraTimeline();
    }

    private static ModifierKeys ToModifierKeys(uint nativeModifiers)
    {
        ModifierKeys result = ModifierKeys.None;
        if ((nativeModifiers & NativeRendererNative.InputModifierShift) != 0) result |= ModifierKeys.Shift;
        if ((nativeModifiers & NativeRendererNative.InputModifierControl) != 0) result |= ModifierKeys.Control;
        if ((nativeModifiers & NativeRendererNative.InputModifierAlt) != 0) result |= ModifierKeys.Alt;
        if ((nativeModifiers & NativeRendererNative.InputModifierWindows) != 0) result |= ModifierKeys.Windows;
        return result;
    }

    private static uint ToPixelExtent(double value)
    {
        if (!double.IsFinite(value) || value <= 0d)
            return 1u;
        return checked((uint)Math.Clamp(Math.Ceiling(value), 1d, uint.MaxValue));
    }
}
