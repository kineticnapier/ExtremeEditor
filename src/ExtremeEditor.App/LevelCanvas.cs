using System.Diagnostics;
using System.Numerics;
using ExtremeEditor.Core;

namespace ExtremeEditor.App;

public sealed class LevelCanvas : Control
{
    private const float MinZoom = 0.05f;
    private const float MaxZoom = 400f;
    private const float FloorRadiusPixels = 5f;
    private const int MaxIndividualDraw = 80_000;

    private readonly List<int> _candidates = new(4096);
    private LevelDocument? _level;
    private SpatialGridIndex? _index;
    private Vector2 _camera;
    private float _zoom = 28f;
    private bool _panning;
    private Point _lastMouse;
    private int _selectedFloor = -1;

    public event Action? DiagnosticsChanged;

    public LevelDocument? Level => _level;
    public int LastCandidateCount { get; private set; }
    public int LastDrawnCount { get; private set; }
    public double LastPaintMilliseconds { get; private set; }

    public int SelectedFloor
    {
        get => _selectedFloor;
        set
        {
            if (_selectedFloor == value) return;
            _selectedFloor = value;
            Invalidate();
        }
    }

    public LevelCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(20, 22, 26);
        ForeColor = Color.Gainsboro;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint, true);
    }

    public void SetLevel(LevelDocument level, SpatialGridIndex index)
    {
        _level = level;
        _index = index;
        _selectedFloor = -1;
        FrameAll();
    }

    public void Reindex(bool keepCamera = true)
    {
        if (_level is null) return;
        _index = new SpatialGridIndex(_level.Positions);
        if (!keepCamera) FrameAll();
        else Invalidate();
    }

    public void FrameAll()
    {
        if (_level is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        WorldRect b = _level.Bounds;
        _camera = new Vector2((b.Left + b.Right) * .5f, (b.Top + b.Bottom) * .5f);

        float zx = (ClientSize.Width - 80f) / Math.Max(1f, b.Width);
        float zy = (ClientSize.Height - 80f) / Math.Max(1f, b.Height);
        _zoom = Math.Clamp(Math.Min(zx, zy), MinZoom, MaxZoom);
        Invalidate();
    }

    public int QueryCurrentViewport()
    {
        if (_index is null) return 0;
        _index.Query(GetViewportWorldRect().Inflate(2f), _candidates);
        return _candidates.Count;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var watch = Stopwatch.StartNew();

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        e.Graphics.Clear(BackColor);

        if (_level is null || _index is null)
        {
            DrawCenteredText(e.Graphics, "Open an .adofai file");
            return;
        }

        WorldRect viewport = GetViewportWorldRect();
        _index.Query(viewport.Inflate(2f), _candidates);
        LastCandidateCount = _candidates.Count;

        // When zoomed far out, drawing tens of thousands of tiny circles is
        // pointless. Render a sampled overview instead. Normal editing zoom
        // still draws every candidate in the viewport.
        int stride = Math.Max(1, (_candidates.Count + MaxIndividualDraw - 1) / MaxIndividualDraw);
        LastDrawnCount = 0;

        using var pathPen = new Pen(Color.FromArgb(100, 155, 165, 180), Math.Max(1f, _zoom * .055f));
        using var floorBrush = new SolidBrush(Color.FromArgb(220, 220, 225, 235));
        using var selectedBrush = new SolidBrush(Color.FromArgb(255, 255, 210, 80));

        Vector2[] positions = _level.Positions;

        // Connect visible-adjacent floors where both endpoints are in/near the viewport.
        // This avoids constructing one retained UI object per floor.
        for (int c = 0; c < _candidates.Count; c += stride)
        {
            int i = _candidates[c];
            if ((uint)i >= (uint)positions.Length)
                continue;

            Vector2 p = positions[i];
            if (!viewport.Inflate(1f).Contains(p))
                continue;

            PointF screen = WorldToScreen(p);

            if (i + 1 < positions.Length)
            {
                Vector2 next = positions[i + 1];
                if (viewport.Inflate(2f).Contains(next))
                    e.Graphics.DrawLine(pathPen, screen, WorldToScreen(next));
            }

            float r = Math.Clamp(FloorRadiusPixels, 2f, 8f);
            Brush brush = i == _selectedFloor ? selectedBrush : floorBrush;
            e.Graphics.FillEllipse(brush, screen.X - r, screen.Y - r, r * 2, r * 2);
            LastDrawnCount++;
        }

        watch.Stop();
        LastPaintMilliseconds = watch.Elapsed.TotalMilliseconds;
        DiagnosticsChanged?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Vector2 before = ScreenToWorld(e.Location);
        float factor = e.Delta > 0 ? 1.18f : 1f / 1.18f;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        Vector2 after = ScreenToWorld(e.Location);
        _camera += before - after;
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _lastMouse = e.Location;

        if (e.Button is MouseButtons.Middle or MouseButtons.Right)
        {
            _panning = true;
            Cursor = Cursors.Hand;
            Capture = true;
        }
        else if (e.Button == MouseButtons.Left)
        {
            SelectNearest(e.Location);
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panning)
        {
            var delta = new Point(e.X - _lastMouse.X, e.Y - _lastMouse.Y);
            _camera -= new Vector2(delta.X / _zoom, -delta.Y / _zoom);
            _lastMouse = e.Location;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            Cursor = Cursors.Default;
            Capture = false;
        }
        base.OnMouseUp(e);
    }

    private void SelectNearest(Point screenPoint)
    {
        if (_level is null || _index is null)
            return;

        Vector2 world = ScreenToWorld(screenPoint);
        float radiusWorld = 12f / _zoom;
        _index.Query(new WorldRect(
            world.X - radiusWorld, world.Y - radiusWorld,
            world.X + radiusWorld, world.Y + radiusWorld), _candidates);

        float bestSq = radiusWorld * radiusWorld;
        int best = -1;
        foreach (int i in _candidates)
        {
            float d = Vector2.DistanceSquared(world, _level.Positions[i]);
            if (d <= bestSq)
            {
                bestSq = d;
                best = i;
            }
        }

        SelectedFloor = best;
    }

    private WorldRect GetViewportWorldRect()
    {
        float halfW = ClientSize.Width * .5f / _zoom;
        float halfH = ClientSize.Height * .5f / _zoom;
        return new WorldRect(
            _camera.X - halfW,
            _camera.Y - halfH,
            _camera.X + halfW,
            _camera.Y + halfH);
    }

    private PointF WorldToScreen(Vector2 p) =>
        new(
            (p.X - _camera.X) * _zoom + ClientSize.Width * .5f,
            (_camera.Y - p.Y) * _zoom + ClientSize.Height * .5f);

    private Vector2 ScreenToWorld(Point p) =>
        new(
            (p.X - ClientSize.Width * .5f) / _zoom + _camera.X,
            _camera.Y - (p.Y - ClientSize.Height * .5f) / _zoom);

    private void DrawCenteredText(Graphics g, string text)
    {
        SizeF size = g.MeasureString(text, Font);
        g.DrawString(text, Font, SystemBrushes.ControlLight,
            (ClientSize.Width - size.Width) * .5f,
            (ClientSize.Height - size.Height) * .5f);
    }
}
