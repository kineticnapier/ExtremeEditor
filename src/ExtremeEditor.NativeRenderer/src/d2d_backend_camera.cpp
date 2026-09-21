#include "d2d_backend.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <functional>

namespace ee
{
namespace
{
D2D1_POINT_2F WorldToScreen(
    float world_x,
    float world_y,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    std::uint32_t width,
    std::uint32_t height) noexcept
{
    const float dx = world_x - camera_x;
    const float dy = world_y - camera_y;
    const float c = std::cos(camera_rotation);
    const float s = std::sin(camera_rotation);
    const float view_x = c * dx + s * dy;
    const float view_y = -s * dx + c * dy;
    return D2D1::Point2F(
        view_x * zoom + static_cast<float>(width) * 0.5f,
        -view_y * zoom + static_cast<float>(height) * 0.5f);
}
}

void D2DBackend::QueryVisibleFloorsCamera(
    const LevelScene& scene,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    RenderFrameStats& stats) noexcept
{
    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float view_half_width = static_cast<float>(width_) * 0.5f / zoom;
    const float view_half_height = static_cast<float>(height_) * 0.5f / zoom;
    const float c = std::abs(std::cos(camera_rotation));
    const float s = std::abs(std::sin(camera_rotation));
    const float half_width = c * view_half_width + s * view_half_height;
    const float half_height = s * view_half_width + c * view_half_height;
    constexpr float margin = 2.5f;

    const auto started = std::chrono::steady_clock::now();
    scene.Query(
        camera_x - half_width - margin,
        camera_y - half_height - margin,
        camera_x + half_width + margin,
        camera_y + half_height + margin,
        visible_candidates_);
    std::sort(visible_candidates_.begin(), visible_candidates_.end(), std::greater<>());
    const auto finished = std::chrono::steady_clock::now();

    stats.cull_ms = std::chrono::duration<double, std::milli>(finished - started).count();
    stats.visible_candidates = static_cast<std::uint32_t>(
        std::min<std::size_t>(visible_candidates_.size(), UINT32_MAX));
}

void D2DBackend::DrawSceneOverlaysCamera(
    const LevelScene& scene,
    const IconAssetTable* icon_assets,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    std::int32_t selected_floor,
    RenderFrameStats& stats) noexcept
{
    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float selection_world = 2.0f / zoom;
    const float cc = std::cos(camera_rotation);
    const float cs = std::sin(camera_rotation);

    if (selected_floor >= 0)
    {
        const std::uint32_t selected = static_cast<std::uint32_t>(selected_floor);
        if (std::find(visible_candidates_.begin(), visible_candidates_.end(), selected) != visible_candidates_.end() &&
            selected < scene.floors.size())
        {
            const EeFloor& floor = scene.floors[selected];
            if (floor.geometry_id < floor_geometries_.size())
            {
                const D2D1_POINT_2F center = WorldToScreen(
                    floor.x, floor.y, camera_x, camera_y, zoom, camera_rotation, width_, height_);
                const float fc = std::cos(floor.entry_angle);
                const float fs = std::sin(floor.entry_angle);
                d2d_context_->SetTransform(D2D1::Matrix3x2F(
                    zoom * (cc * fc + cs * fs),
                    zoom * (cs * fc - cc * fs),
                    zoom * (-cc * fs + cs * fc),
                    zoom * (-cs * fs - cc * fc),
                    center.x,
                    center.y));
                d2d_context_->DrawGeometry(
                    floor_geometries_[floor.geometry_id].Get(),
                    selection_brush_.Get(),
                    selection_world);
                ++stats.draw_calls;
            }
        }
    }

    if (zoom >= 12.0f && icon_assets != nullptr)
    {
        for (std::uint32_t floor_index : visible_candidates_)
        {
            if (floor_index >= scene.floors.size())
                continue;
            const EeFloor& floor = scene.floors[floor_index];
            if (floor.icon_id == EE_ICON_NONE)
                continue;

            IconBitmapSet* bitmaps = GetIconBitmaps(floor.icon_id, icon_assets);
            if (bitmaps == nullptr || !bitmaps->image)
                continue;

            const D2D1_POINT_2F center = WorldToScreen(
                floor.x, floor.y, camera_x, camera_y, zoom, camera_rotation, width_, height_);
            const bool is_floor_icon = (floor.icon_flags & EE_ICON_FLAG_FLOOR) != 0;
            const bool flipped = (floor.icon_flags & EE_ICON_FLAG_FLIPPED) != 0;
            const float size = zoom * (is_floor_icon ? 0.78f : 0.62f);
            const float icon_angle = floor.icon_angle + camera_rotation;

            if (is_floor_icon && bitmaps->outline)
            {
                DrawBitmapCentered(
                    bitmaps->outline.Get(), center.x, center.y, size * 1.04f, icon_angle, flipped);
                ++stats.draw_calls;
            }
            DrawBitmapCentered(bitmaps->image.Get(), center.x, center.y, size, icon_angle, flipped);
            ++stats.icon_draws;
            ++stats.draw_calls;
        }
    }

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
}

void D2DBackend::DrawPlaybackPlanetsCamera(
    const PlaybackVisualState& playback,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation) noexcept
{
    if (!playback.active)
        return;

    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float radius = std::clamp(zoom * 0.28f, 6.0f, 28.0f);
    const D2D1_POINT_2F stationary = WorldToScreen(
        playback.stationary_x,
        playback.stationary_y,
        camera_x,
        camera_y,
        zoom,
        camera_rotation,
        width_,
        height_);
    const D2D1_POINT_2F orbiting = WorldToScreen(
        playback.orbiting_x,
        playback.orbiting_y,
        camera_x,
        camera_y,
        zoom,
        camera_rotation,
        width_,
        height_);

    ID2D1SolidColorBrush* stationary_brush = playback.stationary_is_red
        ? planet_red_brush_.Get()
        : planet_blue_brush_.Get();
    ID2D1SolidColorBrush* orbiting_brush = playback.stationary_is_red
        ? planet_blue_brush_.Get()
        : planet_red_brush_.Get();

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
    const D2D1_ELLIPSE stationary_ellipse = D2D1::Ellipse(stationary, radius, radius);
    const D2D1_ELLIPSE orbiting_ellipse = D2D1::Ellipse(orbiting, radius, radius);
    d2d_context_->FillEllipse(stationary_ellipse, stationary_brush);
    d2d_context_->DrawEllipse(stationary_ellipse, planet_outline_brush_.Get(), 1.5f);
    d2d_context_->FillEllipse(orbiting_ellipse, orbiting_brush);
    d2d_context_->DrawEllipse(orbiting_ellipse, planet_outline_brush_.Get(), 1.5f);
}
}
