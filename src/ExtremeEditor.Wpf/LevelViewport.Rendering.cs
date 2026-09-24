using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public sealed partial class LevelViewport
{
    private static readonly Pen SelectedOverviewPen = CreateSelectedOverviewPen();

    private void DrawMeshPreview(DrawingContext drawingContext, WorldRect nearViewport)
    {
        DrawMeshPreviewFloors(drawingContext, nearViewport);
        if (StaticSceneIconsEnabled)
            DrawMeshPreviewIcons(drawingContext, nearViewport);
    }

    private void DrawMeshPreviewFloors(DrawingContext drawingContext, WorldRect nearViewport)
    {
        LastRenderMode = _floorRenderer.HasImportedAssets ? "floor-textured" : "floor-fallback";
        _floorRenderer.BeginFrame(_zoom);

        Vector2[] positions = _level!.Positions;
        _candidates.Sort(static (a, b) => b.CompareTo(a));

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
                selected: false);

            LastDrawnCount++;
        }
    }

    private void DrawMeshPreviewIcons(DrawingContext drawingContext, WorldRect nearViewport)
    {
        if (_level is null || !StaticSceneIconsEnabled)
            return;

        Vector2[] positions = _level.Positions;
        LevelActionStore store = _level.ActionStore;
        for (int actionFloorIndex = 0; actionFloorIndex < store.ActionFloorCount; actionFloorIndex++)
        {
            int floor = store.GetFloor(actionFloorIndex);
            if ((uint)floor >= (uint)positions.Length)
                continue;

            Vector2 position = positions[floor];
            if (!nearViewport.Contains(position))
                continue;

            Point center = WorldToScreen(position);
            GetFloorAngles(floor, positions, out float entryAngle, out float exitAngle);
            bool midSpin = floor < _level.Angles.Length && Math.Abs(_level.Angles[floor] - 999.0) < 0.000001;
            DrawFloorIcon(drawingContext, floor, center, entryAngle, exitAngle, midSpin);
        }
    }

    private bool DrawFloorIcon(
        DrawingContext drawingContext,
        int floor,
        Point center,
        float entryAngle,
        float exitAngle,
        bool midSpin)
    {
        if (_level is null || !_level.ActionStore.TryGetActions(floor, out ReadOnlySpan<LevelAction> actions))
            return false;

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
            switch (action.Kind)
            {
                case LevelActionKind.SetFloorIcon when customIconAction is null:
                    customIconAction = action;
                    break;
                case LevelActionKind.Checkpoint:
                    checkpoint = true;
                    break;
                case LevelActionKind.Twirl:
                    twirl = true;
                    break;
                case LevelActionKind.SetSpeed when speedAction is null:
                    speedAction = action;
                    break;
            }
        }

        if (!hasActiveAction)
            return false;

        if (customIconAction?.CustomIcon is { Length: > 0 } customIcon &&
            _iconRenderer.DrawFloorIcon(drawingContext, customIcon, center, _zoom))
            return true;

        if (checkpoint && _iconRenderer.DrawFloorIcon(drawingContext, "Checkpoint", center, _zoom))
            return true;

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
                return true;
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
                return true;
        }

        foreach (LevelAction action in actions)
        {
            if (action.Active && _iconRenderer.DrawEvent(drawingContext, action.EventType, center, _zoom))
                return true;
        }

        return false;
    }

    private void DrawOverview(DrawingContext drawingContext, WorldRect nearViewport)
    {
        LastRenderMode = "dots";
        int stride = Math.Max(1, (_candidates.Count + MaxIndividualDraw - 1) / MaxIndividualDraw);
        double pathWidth = Math.Max(1.0, _zoom * 0.055);
        var pathPen = new Pen(PathBrush, pathWidth);
        pathPen.Freeze();
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
                drawingContext.DrawLine(pathPen, screen, WorldToScreen(positions[floor + 1]));

            drawingContext.DrawEllipse(
                FloorBrush,
                null,
                screen,
                FloorRadiusPixels,
                FloorRadiusPixels);

            LastDrawnCount++;
        }
    }

    private void DrawSelectedFloorOverlay(DrawingContext drawingContext)
    {
        if (_level is null || _selectionState.SelectedFloors.Count == 0)
            return;

        Vector2[] positions = _level.Positions;
        bool meshPreview = _useFloorPreview && _zoom >= MinMeshPreviewZoom;
        if (meshPreview)
            _floorRenderer.BeginFrame(_zoom);

        foreach (int floor in _selectionState.SelectedFloors)
        {
            if ((uint)floor >= (uint)positions.Length)
                continue;

            Point center = WorldToScreen(positions[floor]);
            if (meshPreview)
            {
                GetFloorAngles(floor, positions, out float entryAngle, out float exitAngle);
                bool midSpin = floor < _level.Angles.Length &&
                               Math.Abs(_level.Angles[floor] - 999.0) < 0.000001;
                _floorRenderer.DrawSelectionOutline(
                    drawingContext,
                    center,
                    _zoom,
                    entryAngle,
                    exitAngle,
                    midSpin);
            }
            else
            {
                drawingContext.DrawEllipse(
                    SelectedFloorBrush,
                    SelectedOverviewPen,
                    center,
                    FloorRadiusPixels + 3.0,
                    FloorRadiusPixels + 3.0);
            }
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
        LevelActionStore store = _level.ActionStore;
        int actionFloorIndex = 0;
        while (actionFloorIndex < store.ActionFloorCount && store.GetFloor(actionFloorIndex) < 0)
            actionFloorIndex++;

        for (int floor = 0; floor < _floorIsCcw.Length; floor++)
        {
            if (actionFloorIndex < store.ActionFloorCount && store.GetFloor(actionFloorIndex) == floor)
            {
                foreach (LevelAction action in store.GetActionsAt(actionFloorIndex++))
                {
                    if (action.Active && action.Kind == LevelActionKind.Twirl)
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

    private void SelectNearest(Point screenPoint, ModifierKeys modifiers)
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

        if (bestFloor >= 0)
            SelectFloor(bestFloor, modifiers);
        else if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
            SetSelection([]);
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

    private Point WorldToScreen(Vector2 point)
    {
        Vector2 camera = _renderCameraOverride ?? _camera;
        return new Point(
            (point.X - camera.X) * _zoom + ActualWidth * 0.5,
            (camera.Y - point.Y) * _zoom + ActualHeight * 0.5);
    }

    private Vector2 ScreenToWorld(Point point) =>
        new(
            (float)((point.X - ActualWidth * 0.5) / _zoom + _camera.X),
            (float)(_camera.Y - (point.Y - ActualHeight * 0.5) / _zoom));

    private static Pen CreateSelectedOverviewPen()
    {
        var pen = new Pen(SelectedFloorOutlineBrush, 2.0);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private readonly record struct SwirlVisual(bool IsRed, bool Flipped, float IconAngle);
}
