using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

public sealed class NativeLevelViewport : HwndHost
{
    private NativeRendererSession? _session;
    private LevelDocument? _level;
    private NativeLevelSnapshot? _snapshot;
    private NativePlaybackTiming[] _playbackTimeline = [];
    private bool _frameAllPending;
    private bool _followPlayer = true;

    public NativeLevelViewport()
    {
        Focusable = false;
        SizeChanged += (_, _) => ResizeNativeChild();
    }

    public event Action<int>? SelectedFloorChanged;
    public event Action<bool>? FollowPlayerChanged;

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
        _level = level;
        _snapshot = null;
        UploadPendingLevel();
    }

    public void SetPlaybackTimeline(TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(timingMap);
        _playbackTimeline = NativePlaybackTimelineBuilder.Build(timingMap);
        UploadPendingPlaybackTimeline();
    }

    public void SetPlaybackState(double chartTime, double chartRate, bool active, bool playing)
    {
        _session?.SetPlaybackAnchor(chartTime, chartRate, active, playing);
    }

    public void ClearPlayback()
    {
        _session?.SetPlaybackAnchor(0.0, 1.0, active: false, playing: false);
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
        _session.SetFollowPlayer(_followPlayer);
        UploadPendingLevel();
        UploadPendingPlaybackTimeline();
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
        }
        _session?.Dispose();
        _session = null;
    }

    private void NativeSelectionChanged(int floor)
    {
        SelectedFloorChanged?.Invoke(floor);
    }

    private void NativeFollowPlayerChanged(bool enabled)
    {
        if (_followPlayer == enabled)
            return;

        _followPlayer = enabled;
        FollowPlayerChanged?.Invoke(enabled);
    }

    private void UploadPendingLevel()
    {
        if (_session is null || _level is null)
            return;

        _snapshot ??= NativeLevelSnapshotBuilder.Build(_level);
        _session.SetLevel(_snapshot);
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

    private static uint ToPixelExtent(double value)
    {
        if (!double.IsFinite(value) || value <= 0d)
            return 1u;

        return checked((uint)Math.Clamp(Math.Ceiling(value), 1d, uint.MaxValue));
    }
}
