#include "renderer.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <vector>

namespace ee
{
bool Renderer::GetDiagnostics(EeRendererDiagnostics& diagnostics) noexcept
{
    std::shared_ptr<LevelScene> scene;
    std::shared_ptr<const IconAssetTable> icon_assets;
    float camera_x = 0.0f;
    float camera_y = 0.0f;
    float zoom = 28.0f;
    std::int32_t selected_floor = -1;
    bool playback_active = false;
    {
        std::lock_guard lock(scene_mutex_);
        scene = scene_;
        icon_assets = icon_assets_;
        camera_x = camera_x_;
        camera_y = camera_y_;
        zoom = zoom_;
        selected_floor = selected_floor_;
        playback_active = playback_active_;
    }

    const auto now = std::chrono::steady_clock::now();
    const std::uint64_t frame_counter = frame_counter_.load(std::memory_order_relaxed);
    double fps = 0.0;
    {
        std::lock_guard lock(diagnostics_mutex_);
        if (diagnostics_last_sample_.time_since_epoch().count() != 0)
        {
            const double elapsed = std::chrono::duration<double>(now - diagnostics_last_sample_).count();
            if (elapsed > 1e-6)
                fps = static_cast<double>(frame_counter - diagnostics_last_frame_counter_) / elapsed;
        }
        diagnostics_last_sample_ = now;
        diagnostics_last_frame_counter_ = frame_counter;
    }

    diagnostics.fps = fps;
    diagnostics.frame_ms = last_frame_ms_.load(std::memory_order_relaxed);
    diagnostics.max_frame_ms = max_frame_ms_.exchange(0.0, std::memory_order_acq_rel);
    diagnostics.render_ms = last_render_ms_.load(std::memory_order_relaxed);
    diagnostics.cull_ms = 0.0;
    diagnostics.visible_candidates = 0;
    diagnostics.floor_draws = 0;
    diagnostics.icon_draws = 0;
    diagnostics.draw_calls = 0;

    const std::uint32_t width = width_.load(std::memory_order_relaxed);
    const std::uint32_t height = height_.load(std::memory_order_relaxed);
    diagnostics.draw_calls =
        static_cast<std::uint32_t>(width / 64u + 1u) +
        static_cast<std::uint32_t>(height / 64u + 1u) +
        1u; // viewport border

    if (!scene || scene->floors.empty())
    {
        diagnostics.draw_calls += 1u; // idle pulse
        return true;
    }

    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float half_width = static_cast<float>(width) * 0.5f / zoom;
    const float half_height = static_cast<float>(height) * 0.5f / zoom;
    constexpr float margin = 2.5f;

    std::vector<std::uint32_t> candidates;
    const auto cull_start = std::chrono::steady_clock::now();
    scene->Query(
        camera_x - half_width - margin,
        camera_y - half_height - margin,
        camera_x + half_width + margin,
        camera_y + half_height + margin,
        candidates);
    const auto cull_end = std::chrono::steady_clock::now();
    diagnostics.cull_ms = std::chrono::duration<double, std::milli>(cull_end - cull_start).count();
    diagnostics.visible_candidates = static_cast<std::uint32_t>(
        std::min<std::size_t>(candidates.size(), UINT32_MAX));

    constexpr std::size_t MaxIndividualDraw = 80000;
    const std::size_t stride = std::max<std::size_t>(
        1,
        (candidates.size() + MaxIndividualDraw - 1) / MaxIndividualDraw);

    bool selection_drawn = false;
    std::uint32_t floor_draws = 0;
    for (std::size_t candidate_index = 0; candidate_index < candidates.size(); candidate_index += stride)
    {
        const std::uint32_t floor_index = candidates[candidate_index];
        if (floor_index >= scene->floors.size())
            continue;
        const EeFloor& floor = scene->floors[floor_index];
        if (floor.geometry_id >= scene->geometries.size())
            continue;

        ++floor_draws;
        if (selected_floor >= 0 && floor_index == static_cast<std::uint32_t>(selected_floor))
            selection_drawn = true;
    }
    diagnostics.floor_draws = floor_draws;
    diagnostics.draw_calls += floor_draws * 2u;
    if (selection_drawn)
        diagnostics.draw_calls += 1u;

    if (zoom >= 12.0f && icon_assets)
    {
        std::uint32_t icon_draws = 0;
        for (std::size_t candidate_index = 0; candidate_index < candidates.size(); candidate_index += stride)
        {
            const std::uint32_t floor_index = candidates[candidate_index];
            if (floor_index >= scene->floors.size())
                continue;

            const EeFloor& floor = scene->floors[floor_index];
            if (floor.icon_id == EE_ICON_NONE)
                continue;

            const auto asset = icon_assets->find(floor.icon_id);
            if (asset == icon_assets->end())
                continue;

            ++icon_draws;
            ++diagnostics.draw_calls; // icon image
            if ((floor.icon_flags & EE_ICON_FLAG_FLOOR) != 0 && !asset->second.outline_path.empty())
                ++diagnostics.draw_calls;
        }
        diagnostics.icon_draws = icon_draws;
    }

    if (playback_active)
        diagnostics.draw_calls += 4u; // two fills + two outlines

    return true;
}
}
