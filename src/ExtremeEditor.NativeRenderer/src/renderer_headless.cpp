#include "renderer.h"
#include "d2d_backend.h"
#include "renderer_camera.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <memory>

namespace ee
{
namespace
{
constexpr float PlanetDistance = 1.5f;

PlaybackVisualState CalculateHeadlessPlaybackVisual(
    const LevelScene* scene,
    const std::vector<EePlaybackTiming>* timings,
    double chart_time) noexcept
{
    PlaybackVisualState result;
    if (scene == nullptr || timings == nullptr || timings->empty() || scene->floors.empty())
        return result;

    const double pose_time = std::max(0.0, chart_time);
    const auto upper = std::upper_bound(
        timings->begin(),
        timings->end(),
        pose_time,
        [](double value, const EePlaybackTiming& timing)
        {
            return value < timing.entry_time;
        });

    std::size_t floor = upper == timings->begin()
        ? 0
        : static_cast<std::size_t>(std::distance(timings->begin(), upper) - 1);
    floor = std::min(floor, scene->floors.size() - 1);
    floor = std::min(floor, timings->size() - 1);

    const EePlaybackTiming& timing = (*timings)[floor];
    const double duration = timing.exit_time - timing.entry_time;
    const double rotation_duration = std::max(0.0, duration - timing.pause_seconds);
    const double rotation_elapsed = std::max(
        0.0,
        pose_time - timing.entry_time - timing.pause_seconds);
    const double progress = rotation_duration <= 1e-9
        ? 1.0
        : std::clamp(rotation_elapsed / rotation_duration, 0.0, 1.0);

    const EeFloor& stationary = scene->floors[floor];
    const double direction = (timing.flags & EE_PLAYBACK_TIMING_FLAG_CCW) != 0u ? -1.0 : 1.0;
    const double angle = static_cast<double>(timing.entry_angle) +
        direction * static_cast<double>(timing.angle_moved) * progress;

    result.active = true;
    result.floor = static_cast<std::int32_t>(floor);
    result.stationary_x = stationary.x;
    result.stationary_y = stationary.y;
    result.orbiting_x = stationary.x + static_cast<float>(std::sin(angle)) * PlanetDistance;
    result.orbiting_y = stationary.y + static_cast<float>(std::cos(angle)) * PlanetDistance;
    result.stationary_is_red = (floor & 1u) == 0u;
    return result;
}
}

struct HeadlessRendererState
{
    bool com_owned = false;
    D2DBackend backend;

    ~HeadlessRendererState()
    {
        // Release all COM-backed renderer resources before balancing COM on the
        // caller thread that created this synchronous headless session.
        backend.Shutdown();
        if (com_owned)
            CoUninitialize();
    }
};

bool Renderer::InitializeHeadless(std::uint32_t width, std::uint32_t height) noexcept
{
    StopRenderThread();
    headless_state_.reset();

    const HRESULT com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(com_result) && com_result != RPC_E_CHANGED_MODE)
        return false;

    try
    {
        auto state = std::make_shared<HeadlessRendererState>();
        state->com_owned = SUCCEEDED(com_result);
        const std::uint32_t safe_width = std::max<std::uint32_t>(1u, width);
        const std::uint32_t safe_height = std::max<std::uint32_t>(1u, height);
        if (!state->backend.InitializeOffscreen(safe_width, safe_height))
            return false;

        width_.store(safe_width, std::memory_order_relaxed);
        height_.store(safe_height, std::memory_order_relaxed);
        frame_counter_.store(0u, std::memory_order_relaxed);
        last_frame_ms_.store(0.0, std::memory_order_relaxed);
        max_frame_ms_.store(0.0, std::memory_order_relaxed);
        last_render_ms_.store(0.0, std::memory_order_relaxed);
        headless_state_ = std::move(state);
        return true;
    }
    catch (...)
    {
        if (SUCCEEDED(com_result))
            CoUninitialize();
        return false;
    }
}

bool Renderer::RenderHeadlessRgb(
    double scene_time,
    double visual_time,
    std::uint8_t* rgb,
    std::uint32_t rgb_size,
    std::uint32_t row_stride) noexcept
{
    if (!std::isfinite(scene_time) || !std::isfinite(visual_time) || rgb == nullptr)
        return false;

    const auto frame_started = std::chrono::steady_clock::now();

    std::shared_ptr<HeadlessRendererState> state;
    std::shared_ptr<LevelScene> scene;
    std::shared_ptr<const IconAssetTable> icon_assets;
    std::shared_ptr<const std::vector<EePlaybackTiming>> playback_timings;
    std::shared_ptr<const std::vector<EeCameraEvent>> camera_events;
    float camera_x = 0.0f;
    float camera_y = 0.0f;
    float zoom = 28.0f;
    bool follow_player = true;
    std::uint64_t scene_version = 0;
    std::uint64_t icon_assets_version = 0;

    {
        std::lock_guard lock(scene_mutex_);
        state = headless_state_;
        if (!state || !scene_)
            return false;

        if (!pending_icon_states_.empty())
        {
            for (std::size_t i = 0; i < pending_icon_states_.size(); ++i)
            {
                const std::size_t floor_index = static_cast<std::size_t>(pending_icon_start_floor_) + i;
                if (floor_index >= scene_->floors.size())
                    break;
                EeFloor& floor = scene_->floors[floor_index];
                const EeFloorIconState& icon = pending_icon_states_[i];
                floor.icon_id = icon.icon_id;
                floor.icon_flags = icon.icon_flags;
                floor.icon_angle = icon.icon_angle;
            }
            pending_icon_states_.clear();
            ++scene_version_;
        }

        scene = scene_;
        icon_assets = icon_assets_;
        playback_timings = playback_timings_;
        camera_events = camera_events_;
        camera_x = camera_x_;
        camera_y = camera_y_;
        zoom = zoom_;
        follow_player = follow_player_;
        scene_version = scene_version_;
        icon_assets_version = icon_assets_version_;
    }

    // Explicit renders must not inherit realtime anchor jitter. Clear only the
    // anchor bookkeeping, then pin all chart-time runtimes to scene_time without
    // marking them as playing. Rewinds therefore rebuild from their base state.
    scene->SetTrackPlaybackAnchor(0.0, 1.0, 0u);
    scene->SetTrackPlaybackAnchor(scene_time, 1.0, EE_PLAYBACK_FLAG_ACTIVE);
    scene->UpdateTrackTransforms();
    scene->UpdateTrackVisuals();

    const PlaybackVisualState playback = CalculateHeadlessPlaybackVisual(
        scene.get(),
        playback_timings.get(),
        scene_time);

    float render_camera_x = camera_x;
    float render_camera_y = camera_y;
    float render_zoom = zoom;
    float render_camera_rotation = 0.0f;

    if (follow_player && playback.active)
    {
        // B0 uses the existing camera event evaluator but intentionally omits the
        // stateful realtime follow-glide layer. This makes arbitrary seeks exact
        // and deterministic; B1 can reconstruct that glide as a pure scene-time
        // function for stock screenshot comparison.
        const CameraVisualState camera_visual = CalculateCameraVisual(
            scene.get(),
            camera_events.get(),
            playback,
            scene_time);
        if (camera_visual.active)
        {
            render_camera_x = camera_visual.x;
            render_camera_y = camera_visual.y;
            render_zoom = std::clamp(
                zoom * camera_visual.zoom_multiplier,
                0.05f,
                400.0f);
            render_camera_rotation = camera_visual.rotation;
        }
        else
        {
            render_camera_x = playback.stationary_x;
            render_camera_y = playback.stationary_y;
        }
    }

    const auto render_started = std::chrono::steady_clock::now();
    const HRESULT render_result = state->backend.RenderFrameOffscreen(
        visual_time,
        scene.get(),
        scene_version,
        icon_assets.get(),
        icon_assets_version,
        render_camera_x,
        render_camera_y,
        render_zoom,
        render_camera_rotation,
        playback);
    if (FAILED(render_result))
        return false;

    if (!state->backend.ReadbackRgb(rgb, rgb_size, row_stride))
        return false;

    const auto frame_finished = std::chrono::steady_clock::now();
    last_render_ms_.store(
        std::chrono::duration<double, std::milli>(frame_finished - render_started).count(),
        std::memory_order_relaxed);
    const double frame_ms = std::chrono::duration<double, std::milli>(
        frame_finished - frame_started).count();
    last_frame_ms_.store(frame_ms, std::memory_order_relaxed);
    double current_max = max_frame_ms_.load(std::memory_order_relaxed);
    while (current_max < frame_ms &&
           !max_frame_ms_.compare_exchange_weak(
               current_max,
               frame_ms,
               std::memory_order_relaxed,
               std::memory_order_relaxed))
    {
    }
    frame_counter_.fetch_add(1u, std::memory_order_relaxed);
    return true;
}
}
