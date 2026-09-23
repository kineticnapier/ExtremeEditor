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
    diagnostics.visible_candidates = render_stats.visible_candidates;
    diagnostics.floor_draws = render_stats.floor_draws;
    diagnostics.icon_draws = render_stats.icon_draws;
    diagnostics.draw_calls = render_stats.draw_calls;
    return true;
}
}
