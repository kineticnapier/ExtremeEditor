#include "renderer.h"
#include "d2d_backend.h"
#include "renderer_camera.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <limits>
#include <vector>
#include <windowsx.h>

namespace ee
{
namespace
{
constexpr float PlanetDistance = 1.5f;
constexpr double Pi = 3.14159265358979323846;

void UpdateAtomicMax(std::atomic<double>& target, double value) noexcept
{
    double current = target.load(std::memory_order_relaxed);
    while (current < value &&
           !target.compare_exchange_weak(
               current,
               value,
               std::memory_order_relaxed,
               std::memory_order_relaxed))
    {
    }
}

std::uint32_t CaptureInputModifiers(WPARAM mouse_keys) noexcept
{
    std::uint32_t result = 0u;
    if ((mouse_keys & MK_SHIFT) != 0 || (GetKeyState(VK_SHIFT) & 0x8000) != 0)
        result |= EE_INPUT_MODIFIER_SHIFT;
    if ((mouse_keys & MK_CONTROL) != 0 || (GetKeyState(VK_CONTROL) & 0x8000) != 0)
        result |= EE_INPUT_MODIFIER_CONTROL;
    if ((GetKeyState(VK_MENU) & 0x8000) != 0)
        result |= EE_INPUT_MODIFIER_ALT;
    if ((GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0)
        result |= EE_INPUT_MODIFIER_WINDOWS;
    return result;
}

PlaybackVisualState CalculatePlaybackVisual(
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
    const double direction = (timing.flags & EE_PLAYBACK_TIMING_FLAG_CCW) != 0 ? -1.0 : 1.0;
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

double CalculateFollowDuration(
    const std::vector<EePlaybackTiming>* timings,
    std::size_t floor) noexcept
{
    if (timings == nullptr || floor >= timings->size())
        return 0.0;

    const EePlaybackTiming& timing = (*timings)[floor];
    const double rotation_duration = std::max(
        0.0,
        timing.exit_time - timing.entry_time - timing.pause_seconds);
    const double moved = std::abs(static_cast<double>(timing.angle_moved));
    if (rotation_duration > 1e-9 && moved > 1e-6)
    {
        // ADOFAI's normal follow camera uses camspeed = 2 beats. The playback
        // timing already contains SetSpeed/pitch-adjusted seconds, while
        // angle_moved tells us how many beats this floor consumed.
        const double beat_seconds = rotation_duration * Pi / moved;
        return std::max(0.0, beat_seconds * 2.0);
    }

    // Midspins normally do not move the camera target. Keep a sensible fallback
    // for unusual zero-angle floors so a changed target still glides rather than
    // snapping.
    if (floor + 1 < timings->size())
    {
        const double interval = (*timings)[floor + 1].entry_time - timing.entry_time;
        if (interval > 1e-9)
            return interval * 2.0;
    }
    return 0.0;
}

struct FollowCameraState
{
    bool initialized = false;
    std::int32_t floor = -1;
    float x = 0.0f;
    float y = 0.0f;
    float from_x = 0.0f;
    float from_y = 0.0f;
    float to_x = 0.0f;
    float to_y = 0.0f;
    double start_time = 0.0;
    double duration = 0.0;
    double last_chart_time = 0.0;
    std::uint64_t scene_version = 0;

    void Reset() noexcept
    {
        initialized = false;
        floor = -1;
        x = 0.0f;
        y = 0.0f;
        from_x = 0.0f;
        from_y = 0.0f;
        to_x = 0.0f;
        to_y = 0.0f;
        start_time = 0.0;
        duration = 0.0;
        last_chart_time = 0.0;
        scene_version = 0;
    }

    void Evaluate(double chart_time) noexcept
    {
        const double progress = duration <= 1e-9
            ? 1.0
            : std::clamp((chart_time - start_time) / duration, 0.0, 1.0);
        const float t = static_cast<float>(progress);
        x = from_x + (to_x - from_x) * t;
        y = from_y + (to_y - from_y) * t;
    }

    void InitializeAt(
        const PlaybackVisualState& playback,
        double chart_time,
        std::uint64_t version) noexcept
    {
        initialized = true;
        floor = playback.floor;
        x = playback.stationary_x;
        y = playback.stationary_y;
        from_x = x;
        from_y = y;
        to_x = x;
        to_y = y;
        start_time = chart_time;
        duration = 0.0;
        last_chart_time = chart_time;
        scene_version = version;
    }

    void Update(
        const LevelScene* scene,
        const std::vector<EePlaybackTiming>* timings,
        const PlaybackVisualState& playback,
        double chart_time,
        std::uint64_t version) noexcept
    {
        if (!playback.active || scene == nullptr || timings == nullptr || timings->empty())
        {
            Reset();
            return;
        }

        const bool rewound = initialized && chart_time + 1e-7 < last_chart_time;
        const bool changed_scene = initialized && scene_version != version;
        const bool moved_backwards = initialized && playback.floor < floor;
        if (!initialized || rewound || changed_scene || moved_backwards)
        {
            InitializeAt(playback, chart_time, version);
            return;
        }

        const std::int32_t target_floor = playback.floor;
        if (target_floor > floor)
        {
            for (std::int32_t next_floor = floor + 1; next_floor <= target_floor; ++next_floor)
            {
                const std::size_t index = static_cast<std::size_t>(next_floor);
                if (index >= scene->floors.size() || index >= timings->size())
                    break;

                const double transition_time = (*timings)[index].entry_time;
                Evaluate(transition_time);

                from_x = x;
                from_y = y;
                to_x = scene->floors[index].x;
                to_y = scene->floors[index].y;
                start_time = transition_time;
                duration = CalculateFollowDuration(timings, index);
                floor = next_floor;
            }
        }

        // MoveTrack can continue translating the floor after it has been
        // entered. Keep the existing two-beat follow interpolation, but move
        // that whole interpolation frame along with the current floor instead
        // of leaving its destination frozen at the entry-time position.
        if (target_floor == floor)
        {
            const float delta_x = playback.stationary_x - to_x;
            const float delta_y = playback.stationary_y - to_y;
            if (std::abs(delta_x) > 1e-6f || std::abs(delta_y) > 1e-6f)
            {
                x += delta_x;
                y += delta_y;
                from_x += delta_x;
                from_y += delta_y;
                to_x = playback.stationary_x;
                to_y = playback.stationary_y;
            }
        }

        Evaluate(chart_time);
        last_chart_time = chart_time;
        scene_version = version;
    }
};
}

Renderer::~Renderer()
{
    StopRenderThread();
}

bool Renderer::Initialize(HWND parent, std::uint32_t width, std::uint32_t height) noexcept
{
    StopRenderThread();

    const std::uint32_t safe_width = std::max<std::uint32_t>(1u, width);
    const std::uint32_t safe_height = std::max<std::uint32_t>(1u, height);
    if (!window_.Create(parent, safe_width, safe_height, this))
        return false;

    width_.store(safe_width, std::memory_order_relaxed);
    height_.store(safe_height, std::memory_order_relaxed);
    resize_pending_.store(false, std::memory_order_relaxed);
    stop_requested_.store(false, std::memory_order_release);
    frame_counter_.store(0, std::memory_order_relaxed);
    last_frame_ms_.store(0.0, std::memory_order_relaxed);
    max_frame_ms_.store(0.0, std::memory_order_relaxed);
    last_render_ms_.store(0.0, std::memory_order_relaxed);
    {
        std::lock_guard lock(diagnostics_mutex_);
        diagnostics_last_frame_counter_ = 0;
        diagnostics_last_sample_ = {};
    }

    {
        std::lock_guard lock(initialize_mutex_);
        initialize_complete_ = false;
        initialize_success_ = false;
    }

    try
    {
        render_thread_ = std::thread(&Renderer::RenderLoop, this);
    }
    catch (...)
    {
        window_.Destroy();
        return false;
    }

    std::unique_lock lock(initialize_mutex_);
    const bool signaled = initialize_cv_.wait_for(
        lock,
        std::chrono::seconds(5),
        [this] { return initialize_complete_; });

    if (!signaled || !initialize_success_)
    {
        lock.unlock();
        StopRenderThread();
        window_.Destroy();
        return false;
    }

    return true;
}

void Renderer::Resize(std::uint32_t width, std::uint32_t height) noexcept
{
    const std::uint32_t safe_width = std::max<std::uint32_t>(1u, width);
    const std::uint32_t safe_height = std::max<std::uint32_t>(1u, height);

    window_.Resize(safe_width, safe_height);
    width_.store(safe_width, std::memory_order_relaxed);
    height_.store(safe_height, std::memory_order_relaxed);
    resize_pending_.store(true, std::memory_order_release);
}

bool Renderer::SetLevel(std::shared_ptr<LevelScene> scene) noexcept
{
    if (!scene || scene->floors.empty())
        return false;

    std::lock_guard lock(scene_mutex_);
    const bool had_scene = static_cast<bool>(scene_);
    scene_ = std::move(scene);

    if (!had_scene)
    {
        camera_x_ = scene_->floors.front().x;
        camera_y_ = scene_->floors.front().y;
        zoom_ = 28.0f;
    }

    if (selected_floor_ < 0 || static_cast<std::size_t>(selected_floor_) >= scene_->floors.size())
        selected_floor_ = -1;
    playback_active_ = false;
    ++scene_version_;
    return true;
}

void Renderer::FrameAll() noexcept
{
    std::lock_guard lock(scene_mutex_);
    if (!scene_)
        return;

    const float left = scene_->bounds_left;
    const float top = scene_->bounds_top;
    const float right = scene_->bounds_right;
    const float bottom = scene_->bounds_bottom;
    const float bounds_width = std::max(1.0f, right - left);
    const float bounds_height = std::max(1.0f, bottom - top);
    const float available_width = std::max(1.0f, static_cast<float>(width_.load(std::memory_order_relaxed)) - 80.0f);
    const float available_height = std::max(1.0f, static_cast<float>(height_.load(std::memory_order_relaxed)) - 80.0f);

    camera_x_ = (left + right) * 0.5f;
    camera_y_ = (top + bottom) * 0.5f;
    zoom_ = std::clamp(std::min(available_width / bounds_width, available_height / bounds_height), 0.05f, 400.0f);
}

void Renderer::ClearIconAssets() noexcept
{
    std::lock_guard lock(scene_mutex_);
    icon_assets_ = std::make_shared<IconAssetTable>();
    ++icon_assets_version_;
}

bool Renderer::SetIconAsset(
    std::uint32_t icon_id,
    const wchar_t* image_path,
    const wchar_t* outline_path) noexcept
{
    if (image_path == nullptr || *image_path == L'\0')
        return false;

    try
    {
        std::lock_guard lock(scene_mutex_);
        auto next = std::make_shared<IconAssetTable>(icon_assets_ ? *icon_assets_ : IconAssetTable{});
        IconAsset asset;
        asset.image_path = image_path;
        if (outline_path != nullptr)
            asset.outline_path = outline_path;
        (*next)[icon_id] = std::move(asset);
        icon_assets_ = std::move(next);
        ++icon_assets_version_;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

void Renderer::SetSelectionChangedCallback(
    EeSelectionChangedCallback callback,
    void* user_data) noexcept
{
    std::lock_guard lock(scene_mutex_);
    selection_callback_ = callback;
    selection_user_data_ = user_data;
}

bool Renderer::SetPlaybackTimeline(
    const EePlaybackTiming* timings,
    std::uint32_t timing_count) noexcept
{
    if (timing_count > 0 && timings == nullptr)
        return false;

    try
    {
        auto next = std::make_shared<std::vector<EePlaybackTiming>>();
        if (timing_count > 0)
            next->assign(timings, timings + timing_count);

        std::lock_guard lock(scene_mutex_);
        playback_timings_ = std::move(next);
        playback_active_ = false;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

void Renderer::SetPlaybackAnchor(
    double chart_time,
    double chart_rate,
    std::uint32_t flags) noexcept
{
    std::lock_guard lock(scene_mutex_);
    playback_active_ = (flags & EE_PLAYBACK_FLAG_ACTIVE) != 0;
    playback_playing_ = playback_active_ && (flags & EE_PLAYBACK_FLAG_PLAYING) != 0;
    playback_anchor_chart_time_ = std::isfinite(chart_time) ? chart_time : 0.0;
    playback_chart_rate_ = std::isfinite(chart_rate) && chart_rate > 0.0 ? chart_rate : 1.0;
    playback_anchor_steady_ = std::chrono::steady_clock::now();
}

void Renderer::SetFollowPlayer(bool enabled) noexcept
{
    std::lock_guard lock(scene_mutex_);
    follow_player_ = enabled;
}

void Renderer::SetFollowPlayerChangedCallback(
    EeFollowPlayerChangedCallback callback,
    void* user_data) noexcept
{
    std::lock_guard lock(scene_mutex_);
    follow_callback_ = callback;
    follow_user_data_ = user_data;
}

std::int32_t Renderer::SelectedFloor() const noexcept
{
    std::lock_guard lock(scene_mutex_);
    return selected_floor_;
}

std::int32_t Renderer::SelectFloorAt(int screen_x, int screen_y) noexcept
{
    std::lock_guard lock(scene_mutex_);
    if (!scene_ || zoom_ <= 0.0f)
    {
        selected_floor_ = -1;
        return selected_floor_;
    }

    const float width = static_cast<float>(width_.load(std::memory_order_relaxed));
    const float height = static_cast<float>(height_.load(std::memory_order_relaxed));
    const float world_x = (static_cast<float>(screen_x) - width * 0.5f) / zoom_ + camera_x_;
    const float world_y = camera_y_ - (static_cast<float>(screen_y) - height * 0.5f) / zoom_;
    const float radius = std::max(12.0f / zoom_, 0.856f);

    std::vector<std::uint32_t> candidates;
    scene_->Query(
        world_x - radius,
        world_y - radius,
        world_x + radius,
        world_y + radius,
        candidates);

    float best_distance_squared = radius * radius;
    std::int32_t best_floor = -1;
    for (std::uint32_t index : candidates)
    {
        if (index >= scene_->floors.size())
            continue;

        const EeFloor& floor = scene_->floors[index];
        const float dx = floor.x - world_x;
        const float dy = floor.y - world_y;
        const float distance_squared = dx * dx + dy * dy;
        if (distance_squared <= best_distance_squared)
        {
            best_distance_squared = distance_squared;
            best_floor = static_cast<std::int32_t>(index);
        }
    }

    selected_floor_ = best_floor;
    return selected_floor_;
}

void Renderer::NotifySelectionChanged(std::int32_t floor, std::uint32_t modifiers) noexcept
{
    EeSelectionChangedCallback callback = nullptr;
    void* user_data = nullptr;
    {
        std::lock_guard lock(scene_mutex_);
        callback = selection_callback_;
        user_data = selection_user_data_;
    }

    if (callback != nullptr)
        callback(user_data, floor, modifiers);
}

void Renderer::DisableFollowForManualPan() noexcept
{
    bool changed = false;
    {
        std::lock_guard lock(scene_mutex_);
        if (follow_player_)
        {
            follow_player_ = false;
            changed = true;
        }
    }

    if (changed)
        NotifyFollowPlayerChanged(false);
}

void Renderer::NotifyFollowPlayerChanged(bool enabled) noexcept
{
    EeFollowPlayerChangedCallback callback = nullptr;
    void* user_data = nullptr;
    {
        std::lock_guard lock(scene_mutex_);
        callback = follow_callback_;
        user_data = follow_user_data_;
    }

    if (callback != nullptr)
        callback(user_data, enabled ? 1 : 0);
}

LRESULT Renderer::HandleWindowMessage(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept
{
    switch (message)
    {
    case WM_ERASEBKGND:
        return 1;

    case WM_MOUSEWHEEL:
    {
        POINT point{GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
        ScreenToClient(hwnd, &point);
        const int delta = GET_WHEEL_DELTA_WPARAM(wparam);
        if (delta == 0)
            return 0;

        std::lock_guard lock(scene_mutex_);
        const float width = static_cast<float>(width_.load(std::memory_order_relaxed));
        const float height = static_cast<float>(height_.load(std::memory_order_relaxed));
        const float before_x = (static_cast<float>(point.x) - width * 0.5f) / zoom_ + camera_x_;
        const float before_y = camera_y_ - (static_cast<float>(point.y) - height * 0.5f) / zoom_;
        const float steps = static_cast<float>(delta) / static_cast<float>(WHEEL_DELTA);
        const float new_zoom = std::clamp(zoom_ * std::pow(1.18f, steps), 0.05f, 400.0f);
        camera_x_ = before_x - (static_cast<float>(point.x) - width * 0.5f) / new_zoom;
        camera_y_ = before_y + (static_cast<float>(point.y) - height * 0.5f) / new_zoom;
        zoom_ = new_zoom;
        return 0;
    }

    case WM_MBUTTONDOWN:
    case WM_RBUTTONDOWN:
        DisableFollowForManualPan();
        SetFocus(hwnd);
        SetCapture(hwnd);
        panning_ = true;
        pan_button_ = message == WM_MBUTTONDOWN ? WM_MBUTTONUP : WM_RBUTTONUP;
        last_mouse_.x = GET_X_LPARAM(lparam);
        last_mouse_.y = GET_Y_LPARAM(lparam);
        return 0;

    case WM_MOUSEMOVE:
        if (panning_)
        {
            POINT current{GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
            const int dx = current.x - last_mouse_.x;
            const int dy = current.y - last_mouse_.y;
            last_mouse_ = current;

            std::lock_guard lock(scene_mutex_);
            camera_x_ -= static_cast<float>(dx) / zoom_;
            camera_y_ += static_cast<float>(dy) / zoom_;
            return 0;
        }
        break;

    case WM_MBUTTONUP:
    case WM_RBUTTONUP:
        if (panning_ && message == pan_button_)
        {
            panning_ = false;
            pan_button_ = 0;
            if (GetCapture() == hwnd)
                ReleaseCapture();
            return 0;
        }
        break;

    case WM_CAPTURECHANGED:
        panning_ = false;
        pan_button_ = 0;
        break;

    case WM_LBUTTONDOWN:
    {
        const std::uint32_t modifiers = CaptureInputModifiers(wparam);
        SetFocus(hwnd);
        const std::int32_t selected = SelectFloorAt(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
        NotifySelectionChanged(selected, modifiers);
        return 0;
    }
    }

    return DefWindowProcW(hwnd, message, wparam, lparam);
}

void Renderer::RenderLoop() noexcept
{
    const HRESULT com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool com_initialized = SUCCEEDED(com_result);

    D2DBackend backend;
    bool ready = backend.Initialize(
        window_.Handle(),
        width_.load(std::memory_order_relaxed),
        height_.load(std::memory_order_relaxed));

    {
        std::lock_guard lock(initialize_mutex_);
        initialize_success_ = ready;
        initialize_complete_ = true;
    }
    initialize_cv_.notify_all();

    if (!ready)
    {
        if (com_initialized)
            CoUninitialize();
        return;
    }

    const auto start = std::chrono::steady_clock::now();
    FollowCameraState follow_camera;

    while (!stop_requested_.load(std::memory_order_acquire))
    {
        const auto frame_started = std::chrono::steady_clock::now();

        if (resize_pending_.exchange(false, std::memory_order_acq_rel))
        {
            ready = backend.Resize(
                width_.load(std::memory_order_relaxed),
                height_.load(std::memory_order_relaxed));
        }

        std::shared_ptr<LevelScene> scene;
        std::shared_ptr<const IconAssetTable> icon_assets;
        std::shared_ptr<const std::vector<EePlaybackTiming>> playback_timings;
        std::shared_ptr<const std::vector<EeCameraEvent>> camera_events;
        float camera_x = 0.0f;
        float camera_y = 0.0f;
        float zoom = 28.0f;
        std::int32_t selected_floor = -1;
        std::uint64_t scene_version = 0;
        std::uint64_t icon_assets_version = 0;
        bool playback_active = false;
        bool playback_playing = false;
        bool follow_player = false;
        double anchor_chart_time = 0.0;
        double chart_rate = 1.0;
        std::chrono::steady_clock::time_point anchor_steady;
        {
            std::lock_guard lock(scene_mutex_);
            scene = scene_;
            icon_assets = icon_assets_;
            playback_timings = playback_timings_;
            camera_events = camera_events_;
            camera_x = camera_x_;
            camera_y = camera_y_;
            zoom = zoom_;
            selected_floor = selected_floor_;
            scene_version = scene_version_;
            icon_assets_version = icon_assets_version_;
            playback_active = playback_active_;
            playback_playing = playback_playing_;
            follow_player = follow_player_;
            anchor_chart_time = playback_anchor_chart_time_;
            chart_rate = playback_chart_rate_;
            anchor_steady = playback_anchor_steady_;
        }

        if (ready)
        {
            const auto now = std::chrono::steady_clock::now();
            const double seconds = std::chrono::duration<double>(now - start).count();
            double chart_time = anchor_chart_time;
            if (playback_active && playback_playing)
                chart_time += std::chrono::duration<double>(now - anchor_steady).count() * chart_rate;

            // MoveTrack owns its own anchor/rate clock, but the resolved floor
            // transforms still have to be advanced every native render frame.
            // Do this before deriving the planet pose/camera target so all three
            // consumers (track drawing, planets, Follow Player) see the same
            // transformed floor positions for this frame.
            if (scene != nullptr)
                scene->UpdateTrackTransforms();

            PlaybackVisualState playback;
            if (playback_active)
                playback = CalculatePlaybackVisual(scene.get(), playback_timings.get(), chart_time);

            float render_camera_x = camera_x;
            float render_camera_y = camera_y;
            float render_zoom = zoom;
            float render_camera_rotation = 0.0f;
            if (follow_player && playback.active)
            {
                follow_camera.Update(
                    scene.get(),
                    playback_timings.get(),
                    playback,
                    chart_time,
                    scene_version);

                // ADOFAI has a separate smooth follow layer below MoveCamera.
                // Feed the smoothed player pivot into the existing camera event
                // evaluator so Player-relative events inherit the same glide while
                // Tile/Global camera events remain absolute.
                PlaybackVisualState camera_playback = playback;
                camera_playback.stationary_x = follow_camera.x;
                camera_playback.stationary_y = follow_camera.y;

                CameraVisualState camera_visual = CalculateCameraVisual(
                    camera_events.get(), camera_playback, chart_time);
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
                    render_camera_x = follow_camera.x;
                    render_camera_y = follow_camera.y;
                }

                std::lock_guard lock(scene_mutex_);
                if (follow_player_)
                {
                    camera_x_ = render_camera_x;
                    camera_y_ = render_camera_y;
                }
            }
            else
            {
                follow_camera.Reset();
            }

            const auto render_started = std::chrono::steady_clock::now();
            const HRESULT hr = backend.RenderFrame(
                seconds,
                scene.get(),
                scene_version,
                icon_assets.get(),
                icon_assets_version,
                render_camera_x,
                render_camera_y,
                render_zoom,
                render_camera_rotation,
                selected_floor,
                playback);
            const auto render_finished = std::chrono::steady_clock::now();
            last_render_ms_.store(
                std::chrono::duration<double, std::milli>(render_finished - render_started).count(),
                std::memory_order_relaxed);
            ready = SUCCEEDED(hr);
        }

        if (!ready && !stop_requested_.load(std::memory_order_acquire))
        {
            backend.Shutdown();
            std::this_thread::sleep_for(std::chrono::milliseconds(250));
            ready = backend.Initialize(
                window_.Handle(),
                width_.load(std::memory_order_relaxed),
                height_.load(std::memory_order_relaxed));
        }

        const auto frame_finished = std::chrono::steady_clock::now();
        const double frame_ms =
            std::chrono::duration<double, std::milli>(frame_finished - frame_started).count();
        last_frame_ms_.store(frame_ms, std::memory_order_relaxed);
        UpdateAtomicMax(max_frame_ms_, frame_ms);
        frame_counter_.fetch_add(1, std::memory_order_relaxed);
    }

    backend.Shutdown();
    if (com_initialized)
        CoUninitialize();
}

void Renderer::StopRenderThread() noexcept
{
    stop_requested_.store(true, std::memory_order_release);
    if (render_thread_.joinable())
        render_thread_.join();
    window_.Destroy();
}
}
