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
    private const int MaxMeshPreviewDraw = 18_000;
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
    private LevelDocument? _level;
    private SpatialGridIndex? _index;
    private bool[] _floorIsCcw = [];
    private Vector2 _camera;
    private float _zoom = 28f;
    private bool _panning;
    private Point _lastMouse;
    private int _selectedFloor = -1;
    private bool _useFloorPreview = true;

    public int SelectedFloor
    {
        get => _selectedFloor;
        private set
        {
            if (_selectedFloor == value)
                return;

            _selectedFloor = value;
            InvalidateVisual();
        }
    }

    public bool UseFloorPreview
    {
        get => _useFloorPreview;
        set
        {
            if (_useFloorPreview == value)
                return;

            _useFloorPreview = value;
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
        Loaded += (_, _) => FrameAll();
    }

    public void SetLevel(LevelDocument level, SpatialGridIndex index)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(index);

        _level = level;
        _index = index;
        _selectedFloor = -1;
        RebuildFloorDirectionState();
        FrameAll();
        InvalidateVisual();
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
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));

        if (_level is null || _index is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        WorldRect nearViewport = GetViewportWorldRect().Inflate(2f);
        _index.Query(nearViewport, _candidates);
        LastCandidateCount = _candidates.Count;
        LastDrawnCount = 0;

        bool meshPreview = _useFloorPreview &&
                           _zoom >= MinMeshPreviewZoom &&
                           _candidates.Count <= MaxMeshPreviewDraw;

        if (meshPreview)
            DrawMeshPreview(drawingContext, nearViewport);
        else
            DrawOverview(drawingContext, nearViewport);

        DrawPlaybackPlanets(drawingContext);
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
            SelectNearest(_lastMouse);
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
            InvalidateVisual();
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

    private void DrawMeshPreview(DrawingContext drawingContext, WorldRect nearViewport)
    {
        LastRenderMode = _floorRenderer.HasImportedAssets ? "floor-textured" : "floor-fallback";
        _floorRenderer.BeginFrame(_zoom);

        Vector2[] positions = _level!.Positions;
        _candidates.Sort(static (a, b) => b.CompareTo(a));
        bool drawIcons = _zoom >= MinIconZoom &&
                         (_iconRenderer.EventIconCount > 0 || _iconRenderer.FloorIconCount > 0);

        foreach (int floor in _candidates)
        {
            if ((uint)floor >= (uint)positions.Length)
                continue;

            Vector2 position = positions[floor];
            if (!nearViewport.Contains(position))
                continue;

            Point center = WorldToScreen(position);
            GetFloorAngles(floor, positions, out float entryAngle, out float exitAngle);
            bool midSpin = floor < _level.Angles.Length && Math.Abs(_level.Angles[floor] - 999.0) < 0.000001;
            _floorRenderer.DrawFloor(
                drawingContext,
                center,
                _zoom,
                entryAngle,
                exitAngle,
                midSpin,
                floor == _selectedFloor);

            if (drawIcons)
                DrawFloorIcon(drawingContext, floor, center, entryAngle, exitAngle, midSpin);

            LastDrawnCount++;
        }
    }

    private void DrawFloorIcon(
        DrawingContext drawingContext,
        int floor,
        Point center,
        float entryAngle,
        float exitAngle,
        bool midSpin)
    {
        if (_level is null || !_level.ActionsByFloor.TryGetValue(floor, out LevelAction[]? actions))
            return;

        LevelAction? customIconAction = null;
        LevelAction? speedAction = null;
        bool checkpoint = false;
        bool twirl = false;
        bool hasActiveAction = false;

        foreach (LevelAction action in actions)
        {
            if (!action.Active)
                continue;

            hasActiveAction = true;
            if (customIconAction is null && string.Equals(action.EventType, "SetFloorIcon", StringComparison.Ordinal))
                customIconAction = action;
            if (string.Equals(action.EventType, "Checkpoint", StringComparison.Ordinal))
                checkpoint = true;
            if (string.Equals(action.EventType, "Twirl", StringComparison.Ordinal))
                twirl = true;
            if (speedAction is null && string.Equals(action.EventType, "SetSpeed", StringComparison.Ordinal))
                speedAction = action;
        }

        if (!hasActiveAction)
            return;

        if (customIconAction?.CustomIcon is { Length: > 0 } customIcon &&
            _iconRenderer.DrawFloorIcon(drawingContext, customIcon, center, _zoom))
            return;

        if (checkpoint && _iconRenderer.DrawFloorIcon(drawingContext, "Checkpoint", center, _zoom))
            return;

        if (twirl)
        {
            bool isCcw = (uint)floor < (uint)_floorIsCcw.Length && _floorIsCcw[floor];
            SwirlVisual swirl = CalculateSwirlVisual(entryAngle, exitAngle, isCcw, midSpin);
            if (_iconRenderer.DrawFloorIcon(
                    drawingContext,
                    swirl.IsRed ? "SwirlRed" : "SwirlBlue",
                    center,
                    _zoom,
                    swirl.IconAngle,
                    swirl.Flipped))
                return;
        }

        if (speedAction?.SpeedRatio is double ratio)
        {
            string speedIcon = ratio switch
            {
                <= 0.45 => "DoubleSnail",
                < 0.95 => "Snail",
                <= 1.05 => "SameSpeed",
                <= 2.05 => "Rabbit",
                _ => "DoubleRabbit"
            };
            if (_iconRenderer.DrawFloorIcon(drawingContext, speedIcon, center, _zoom))
                return;
        }

        foreach (LevelAction action in actions)
        {
            if (action.Active && _iconRenderer.DrawEvent(drawingContext, action.EventType, center, _zoom))
                return;
        }
    }

    private void DrawOverview(DrawingContext drawingContext, WorldRect nearViewport)
    {
        LastRenderMode = "dots";
        int stride = Math.Max(1, (_candidates.Count + MaxIndividualDraw - 1) / MaxIndividualDraw);
        double pathWidth = Math.Max(1.0, _zoom * 0.055);
        var pathPen = new Pen(PathBrush, pathWidth);
        pathPen.Freeze();
        var selectedOutlinePen = new Pen(SelectedFloorOutlineBrush, 2.0);
        selectedOutlinePen.Freeze();
        Vector2[] positions = _level!.Positions;

        for (int c = 0; c < _candidates.Count; c += stride)
        {
            int floor = _candidates[c];
            if ((uint)floor >= (uint)positions.Length)
                continue;

            Vector2 position = positions[floor];
            if (!nearViewport.Contains(position))
                continue;

            Point screen = WorldToScreen(position);
            if (floor + 1 < positions.Length)
            {
                Vector2 next = positions[floor + 1];
                if (nearViewport.Contains(next))
                    drawingContext.DrawLine(pathPen, screen, WorldToScreen(next));
            }

            if (floor == _selectedFloor)
            {
                drawingContext.DrawEllipse(
                    SelectedFloorBrush,
                    selectedOutlinePen,
                    screen,
                    FloorRadiusPixels + 3.0,
                    FloorRadiusPixels + 3.0);
            }
            else
            {
                drawingContext.DrawEllipse(
                    FloorBrush,
                    null,
                    screen,
                    FloorRadiusPixels,
                    FloorRadiusPixels);
            }

            LastDrawnCount++;
        }
    }

    private void RebuildFloorDirectionState()
    {
        if (_level is null)
        {
            _floorIsCcw = [];
            return;
        }

        _floorIsCcw = new bool[_level.FloorCount];
        bool isCcw = false;
        for (int floor = 0; floor < _floorIsCcw.Length; floor++)
        {
            if (_level.ActionsByFloor.TryGetValue(floor, out LevelAction[]? actions))
            {
                foreach (LevelAction action in actions)
                {
                    if (action.Active && string.Equals(action.EventType, "Twirl", StringComparison.Ordinal))
                        isCcw = !isCcw;
                }
            }

            _floorIsCcw[floor] = isCcw;
        }
    }

    private static SwirlVisual CalculateSwirlVisual(
        float entryScreenAngle,
        float exitScreenAngle,
        bool isCcw,
        bool midSpin)
    {
        float entry = Mod(TwoPi + MathF.PI / 2f - entryScreenAngle, TwoPi);
        float exit = Mod(TwoPi + MathF.PI / 2f - exitScreenAngle, TwoPi);
        float direction = isCcw ? -1f : 1f;
        float moved = Mod((exit - entry) * direction, TwoPi);
        if (MathF.Abs(moved) <= 0.000001f && !midSpin)
            moved = TwoPi;

        bool isRed = moved < 3.1415918f;
        float iconAngle = entry + moved * 0.5f * direction;
        return new SwirlVisual(isRed, isCcw, iconAngle);
    }

    private static float Mod(float value, float modulus)
    {
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    private void SelectNearest(Point screenPoint)
    {
        if (_level is null || _index is null)
            return;

        Vector2 world = ScreenToWorld(screenPoint);
        float radiusWorld = Math.Max(12f / _zoom, FloorSelectionRadiusWorld);
        _index.Query(
            new WorldRect(
                world.X - radiusWorld,
                world.Y - radiusWorld,
                world.X + radiusWorld,
                world.Y + radiusWorld),
            _candidates);

        float bestDistanceSquared = radiusWorld * radiusWorld;
        int bestFloor = -1;
        foreach (int floor in _candidates)
        {
            if ((uint)floor >= (uint)_level.Positions.Length)
                continue;

            float distanceSquared = Vector2.DistanceSquared(world, _level.Positions[floor]);
            if (distanceSquared <= bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestFloor = floor;
            }
        }

        SelectedFloor = bestFloor;
    }

    private static void GetFloorAngles(int floor, Vector2[] positions, out float entryAngle, out float exitAngle)
    {
        Vector2 incoming = Vector2.Zero;
        Vector2 outgoing = Vector2.Zero;

        if (floor > 0)
            incoming = positions[floor - 1] - positions[floor];
        if (floor + 1 < positions.Length)
            outgoing = positions[floor + 1] - positions[floor];

        if (incoming.LengthSquared() < 0.000001f && outgoing.LengthSquared() >= 0.000001f)
            incoming = -outgoing;
        if (outgoing.LengthSquared() < 0.000001f && incoming.LengthSquared() >= 0.000001f)
            outgoing = -incoming;

        entryAngle = incoming.LengthSquared() < 0.000001f
            ? MathF.PI
            : MathF.Atan2(incoming.Y, incoming.X);
        exitAngle = outgoing.LengthSquared() < 0.000001f
            ? 0f
            : MathF.Atan2(outgoing.Y, outgoing.X);
    }

    private WorldRect GetViewportWorldRect()
    {
        float halfWidth = (float)(ActualWidth * 0.5 / _zoom);
        float halfHeight = (float)(ActualHeight * 0.5 / _zoom);
        return new WorldRect(
            _camera.X - halfWidth,
            _camera.Y - halfHeight,
            _camera.X + halfWidth,
            _camera.Y + halfHeight);
    }

    private Point WorldToScreen(Vector2 point) =>
        new(
            (point.X - _camera.X) * _zoom + ActualWidth * 0.5,
            (_camera.Y - point.Y) * _zoom + ActualHeight * 0.5);

    private Vector2 ScreenToWorld(Point point) =>
        new(
            (float)((point.X - ActualWidth * 0.5) / _zoom + _camera.X),
            (float)(_camera.Y - (point.Y - ActualHeight * 0.5) / _zoom));

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private readonly record struct SwirlVisual(bool IsRed, bool Flipped, float IconAngle);
}
