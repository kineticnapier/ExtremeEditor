#pragma once

#include <algorithm>
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

struct PositionTrackDragPreview
{
    bool active = false;
    std::int32_t first_floor = -1;
    std::int32_t last_floor = -1;
    float offset_x = 0.0f;
    float offset_y = 0.0f;

    [[nodiscard]] bool AppliesTo(std::uint32_t floor) const noexcept
    {
        return active &&
            first_floor >= 0 &&
            last_floor >= first_floor &&
            floor >= static_cast<std::uint32_t>(first_floor) &&
            floor <= static_cast<std::uint32_t>(last_floor);
    }
};

struct PositionTrackDragResult
{
    std::int32_t first_floor = -1;
    std::int32_t last_floor = -1;
    bool moved = false;
    InputWorldPoint delta;
};

// HWND-independent gesture state. Freeze the selected range and view at mouse-down
// so hover/selection changes or playback camera motion cannot retarget the edit.
class PositionTrackDrag
{
public:
    bool Active() const noexcept { return first_floor_ >= 0; }

    void Begin(
        std::int32_t first_floor,
        std::int32_t last_floor,
        int x,
        int y,
        int threshold_x,
        int threshold_y,
        const ViewportInputTransform& view) noexcept
    {
        first_floor_ = std::min(first_floor, last_floor);
        last_floor_ = std::max(first_floor, last_floor);
        x_ = x;
        y_ = y;
        threshold_x_ = std::max(1, threshold_x);
        threshold_y_ = std::max(1, threshold_y);
        view_ = view;
        moved_ = false;
        current_delta_ = {};
    }

    void Move(int x, int y) noexcept
    {
        if (!Active())
            return;
        moved_ |= std::abs(x - x_) >= threshold_x_ || std::abs(y - y_) >= threshold_y_;
        current_delta_ = view_.Delta(x - x_, y - y_);
    }

    [[nodiscard]] PositionTrackDragPreview Preview() const noexcept
    {
        if (!Active())
            return {};
        return {
            true,
            first_floor_,
            last_floor_,
            static_cast<float>(current_delta_.x),
            static_cast<float>(current_delta_.y)};
    }

    PositionTrackDragResult End(int x, int y) noexcept
    {
        if (!Active())
            return {};
        Move(x, y);
        const PositionTrackDragResult result{
            first_floor_,
            last_floor_,
            moved_,
            current_delta_};
        Cancel();
        return result;
    }

    void Cancel() noexcept
    {
        first_floor_ = -1;
        last_floor_ = -1;
        moved_ = false;
        current_delta_ = {};
    }

private:
    std::int32_t first_floor_ = -1;
    std::int32_t last_floor_ = -1;
    int x_ = 0, y_ = 0, threshold_x_ = 1, threshold_y_ = 1;
    bool moved_ = false;
    InputWorldPoint current_delta_;
    ViewportInputTransform view_;
};
}
