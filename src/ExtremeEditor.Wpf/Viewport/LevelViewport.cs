using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport : FrameworkElement
{
    private const float MinZoom = 0.05f;
    private const float MaxZoom = 400f;
    private const double FloorRadiusPixels = 4.0;
    private const float MinMeshPreviewZoom = 8f;
    private const float MinIconZoom = 12f;
    private const int MaxIndividualDraw = 80_000;
    private const float FloorSelectionRadiusWorld = 0.856f;
    private const float TwoPi = MathF.PI * 2f;

    private static readonly Brush BackgroundBrush = CreateBrush(20, 22, 26);
    private static readonly Brush FloorBrush = CreateBrush(220, 220, 225, 235);
    private static readonly Brush SelectedFloorBrush = CreateBrush(255, 210, 80);
    private static readonly Brush SelectedFloorOutlineBrush = CreateBrush(255, 235, 155);
    private static readonly Brush PathBrush = CreateBrush(155, 165, 180, 100);

    private readonly List<int> _candidates = new(4096);
    private readonly WpfFloorRenderer _floorRenderer = new();
    private readonly WpfIconRenderer _iconRenderer = new();
    private EditorSelectionState _selectionState = new(0);
    private LevelDocument? _level;
    private SpatialGridIndex? _index;
    private bool[] _floorIsCcw = [];
    private Vector2 _camera;
    private Vector2? _renderCameraOverride;
    private float _zoom = 28f;
    private bool _panning;
    private Point _lastMouse;
    private bool _useFloorPreview = true;

    public EditorSelectionState SelectionState
    {
        get => _selectionState;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_selectionState, value))
                return;

            _selectionState.Changed -= SelectionStateChanged;
            _selectionState = value;
            _selectionState.Changed += SelectionStateChanged;
            if (_level is not null)
                _selectionState.SetFloorCount(_level.FloorCount);

            RenderPlaybackVisual();
            InvalidateVisual();
        }
    }

    public int SelectedFloor => _selectionState.PrimaryFloor;
    public IReadOnlyCollection<int> SelectedFloors => _selectionState.SelectedFloors;
    public event EventHandler? SelectionChanged;

    public bool UseFloorPreview
    {
        get => _useFloorPreview;
        set
        {
            if (_useFloorPreview == value)
                return;

            _useFloorPreview = value;
            ResetStaticScene();
            EnsureSceneCoverage();
            RenderPlaybackVisual();
            InvalidateVisual();
        }
    }

    public int LastCandidateCount { get; private set; }
    public int LastDrawnCount { get; private set; }
    public string LastRenderMode { get; private set; } = "dots";
    public string FloorAssetSummary => _floorRenderer.AssetSummary;
    public string IconAssetSummary => _iconRenderer.Summary;

    public LevelViewport()
    {
        Focusable = true;
        ClipToBounds = true;
        _selectionState.Changed += SelectionStateChanged;
        AddVisualChild(_sceneRoot);
        AddVisualChild(_playbackFloorVisual);
        AddVisualChild(_playbackVisual);
    }

    public void SetLevel(LevelDocument level, SpatialGridIndex index, bool preserveView = false)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(index);

        _level = level;
        _index = index;
        _selectionState.SetFloorCount(level.FloorCount);
        if (!preserveView)
            _selectionState.SetSelection([], -1);
        RebuildFloorDirectionState();

        if (!preserveView)
        {
            _camera = level.Positions.Length > 0 ? level.Positions[0] : Vector2.Zero;
            _zoom = Math.Clamp(Math.Max(28f, MinMeshPreviewZoom), MinZoom, MaxZoom);
        }

        ResetStaticScene();
        EnsureSceneCoverage();
        RenderPlaybackVisual();
        InvalidateVisual();
    }

    public void SetSelection(IEnumerable<int> floors, int primaryFloor = -1)
    {
        _selectionState.SetSelection(floors, primaryFloor);
    }

    public void SelectFloor(int floor, ModifierKeys modifiers = ModifierKeys.None)
    {
        _selectionState.SelectFloor(floor, modifiers);
    }

    public void MoveSelection(int floor, bool extend)
    {
        _selectionState.MoveSelection(floor, extend);
    }

    public void SetSelectedFloorFromExternal(int floor, ModifierKeys modifiers = ModifierKeys.None)
    {
        _selectionState.SelectFloor(floor, modifiers);
    }

    public void FrameAll()
    {
        if (_level is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        WorldRect bounds = _level.Bounds;
        _camera = new Vector2(
            (bounds.Left + bounds.Right) * 0.5f,
            (bounds.Top + bounds.Bottom) * 0.5f);

        double availableWidth = Math.Max(1.0, ActualWidth - 80.0);
        double availableHeight = Math.Max(1.0, ActualHeight - 80.0);
        float zoomX = (float)(availableWidth / Math.Max(1f, bounds.Width));
        float zoomY = (float)(availableHeight / Math.Max(1f, bounds.Height));
        _zoom = Math.Clamp(Math.Min(zoomX, zoomY), MinZoom, MaxZoom);

        if (!TemporalPlaybackActive)
        {
            ResetStaticScene();
            EnsureSceneCoverage();
        }

        RenderPlaybackVisual();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));

        if (_level is null || _index is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        LastCandidateCount = 0;
        LastDrawnCount = 0;
        if (!TemporalPlaybackActive)
        {
            EnsureSceneCoverage();
            UpdateStaticSceneTransform();
        }

        DrawPlaybackPlanets(drawingContext);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_level is null)
            return;

        if (TemporalPlaybackActive)
        {
            RenderPlaybackVisual();
            return;
        }

        ResetStaticScene();
        EnsureSceneCoverage();
        RenderPlaybackVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Focus();
        Point mouse = e.GetPosition(this);
        Vector2 before = ScreenToWorld(mouse);
        float factor = e.Delta > 0 ? 1.18f : 1f / 1.18f;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        Vector2 after = ScreenToWorld(mouse);
        _camera += before - after;

        if (!TemporalPlaybackActive)
        {
            ResetStaticScene();
            EnsureSceneCoverage();
        }

        RenderPlaybackVisual();
        InvalidateVisual();
        e.Handled = true;
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        _lastMouse = e.GetPosition(this);

        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panning = true;
            Cursor = Cursors.Hand;
            Mouse.Capture(this);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            SelectNearest(_lastMouse, Keyboard.Modifiers);
            e.Handled = true;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panning)
        {
            Point current = e.GetPosition(this);
            double dx = current.X - _lastMouse.X;
            double dy = current.Y - _lastMouse.Y;
            _camera -= new Vector2((float)(dx / _zoom), (float)(-dy / _zoom));
            _lastMouse = current;

            if (!TemporalPlaybackActive)
            {
                UpdateStaticSceneTransform();
                EnsureSceneCoverage();
            }

            RenderPlaybackVisual();
            e.Handled = true;
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panning = false;
            Cursor = Cursors.Arrow;
            if (IsMouseCaptured)
                Mouse.Capture(null);
            e.Handled = true;
        }

        base.OnMouseUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _panning = false;
        Cursor = Cursors.Arrow;
        base.OnLostMouseCapture(e);
    }

    private void SelectionStateChanged(object? sender, EventArgs e)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RenderPlaybackVisual();
        InvalidateVisual();
    }
}
