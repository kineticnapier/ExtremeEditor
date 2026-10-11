#pragma once

#include <cmath>
#include <cstdint>

namespace ee
{
struct InputWorldPoint { double x = 0.0; double y = 0.0; };

// Inverse of the renderer's camera rotation, pixel scale and inverted screen Y.
struct ViewportInputTransform
{
    double x = 0.0, y = 0.0, zoom = 28.0, rotation = 0.0;
    double width = 1.0, height = 1.0;

    InputWorldPoint Delta(double dx, double dy) const noexcept
    {
        const double c = std::cos(rotation), s = std::sin(rotation);
        return {(c * dx + s * dy) / zoom, (s * dx - c * dy) / zoom};
    }
    InputWorldPoint Point(double sx, double sy) const noexcept
    {
        const auto delta = Delta(sx - width * 0.5, sy - height * 0.5);
        return {x + delta.x, y + delta.y};
    }
};

struct PositionTrackDragResult
{
    std::int32_t floor = -1;
    bool moved = false;
    InputWorldPoint delta;
};

// HWND-independent gesture state. Freeze the floor and view at mouse-down so
// hover changes or playback camera motion cannot retarget the edit.
class PositionTrackDrag
{
public:
    bool Active() const noexcept { return floor_ >= 0; }
    void Begin(std::int32_t floor, int x, int y, int threshold_x, int threshold_y,
        const ViewportInputTransform& view) noexcept
    {
        floor_ = floor; x_ = x; y_ = y; threshold_x_ = threshold_x; threshold_y_ = threshold_y;
        view_ = view; moved_ = false;
    }
    void Move(int x, int y) noexcept
    {
        if (Active())
            moved_ |= std::abs(x - x_) >= threshold_x_ || std::abs(y - y_) >= threshold_y_;
    }
    PositionTrackDragResult End(int x, int y) noexcept
    {
        if (!Active()) return {};
        Move(x, y);
        const PositionTrackDragResult result{floor_, moved_, view_.Delta(x - x_, y - y_)};
        Cancel();
        return result;
    }
    void Cancel() noexcept { floor_ = -1; moved_ = false; }
private:
    std::int32_t floor_ = -1;
    int x_ = 0, y_ = 0, threshold_x_ = 1, threshold_y_ = 1;
    bool moved_ = false;
    ViewportInputTransform view_;
};
}
