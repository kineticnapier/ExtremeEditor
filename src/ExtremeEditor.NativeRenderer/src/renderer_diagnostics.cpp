#include "renderer.h"
#include "d2d_backend.h"

#include <algorithm>
#include <chrono>
#include <cmath>

namespace ee
{
bool Renderer::GetDiagnostics(EeRendererDiagnostics& diagnostics) noexcept
{
    const double frame_ms = last_frame_ms_.load(std::memory_order_relaxed);
    const auto now = std::chrono::steady_clock::now();
    const std::uint64_t frame_counter = frame_counter_.load(std::memory_order_relaxed);
    double fps = frame_ms > 1e-6 ? 1000.0 / frame_ms : 0.0;
    {
        std::lock_guard lock(diagnostics_mutex_);
        if (diagnostics_last_sample_.time_since_epoch().count() != 0)
        {
            const double elapsed = std::chrono::duration<double>(now - diagnostics_last_sample_).count();
            if (elapsed > 0.01)
                fps = static_cast<double>(frame_counter - diagnostics_last_frame_counter_) / elapsed;
        }
        diagnostics_last_sample_ = now;
        diagnostics_last_frame_counter_ = frame_counter;
    }

    const RenderFrameStats render_stats = GetLatestRenderFrameStats();

    std::shared_ptr<LevelScene> scene;
    {
        std::lock_guard lock(scene_mutex_);
        scene = scene_;
    }
    const TrackTransformUpdateMetrics track_metrics = scene != nullptr
        ? scene->TrackTransformMetrics()
        : TrackTransformUpdateMetrics{};

    diagnostics.reserved = 0u;
    diagnostics.fps = fps;
    diagnostics.frame_ms = frame_ms;
    diagnostics.max_frame_ms = std::max(
        frame_ms,
        max_frame_ms_.exchange(frame_ms, std::memory_order_acq_rel));
    diagnostics.render_ms = last_render_ms_.load(std::memory_order_relaxed);
    diagnostics.cull_ms = render_stats.cull_ms;
    diagnostics.update_ms = render_stats.update_ms;
    diagnostics.draw_setup_ms = render_stats.draw_setup_ms;
    diagnostics.floor_ms = render_stats.floor_ms;
    diagnostics.icon_ms = render_stats.icon_ms;
    diagnostics.overlay_ms = render_stats.overlay_ms;
    diagnostics.end_draw_ms = render_stats.end_draw_ms;
    diagnostics.present_ms = render_stats.present_ms;
    diagnostics.track_total_ms = track_metrics.total_ms;
    diagnostics.track_clock_ms = track_metrics.clock_ms;
    diagnostics.track_evaluate_ms = track_metrics.evaluate_ms;
    diagnostics.track_apply_ms = track_metrics.apply_ms;
    diagnostics.track_spatial_remove_ms = track_metrics.spatial_remove_ms;
    diagnostics.track_spatial_insert_ms = track_metrics.spatial_insert_ms;
    diagnostics.visible_candidates = render_stats.visible_candidates;
    diagnostics.floor_draws = render_stats.floor_draws;
    diagnostics.icon_draws = render_stats.icon_draws;
    diagnostics.draw_calls = render_stats.draw_calls;
    return true;
}
}
