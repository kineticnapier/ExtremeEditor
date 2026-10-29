#include "renderer.h"
#include "shutdown_diagnostics.h"
#include "d2d_backend.h"
#include "follow_camera_state.h"
#include "floor_hit_test.h"
#include "floor_draw_depth.h"
#include "renderer_camera.h"
#include "playback_visual.h"

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


}

Renderer::~Renderer()
{
    ShutdownDiagnosticLogOnce(
        ShutdownDiagnosticPhase::RendererDestructorEntry,
        "Renderer.destructor.begin");
    StopRenderThread(true);
    ShutdownDiagnosticLogOnce(
        ShutdownDiagnosticPhase::RendererDestructorExit,
        "Renderer.destructor.end");
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
    // Every scene created by a full upload may replace the inactive cache.
    // Keep the previous scene only when it was registered from a prepared
    // managed snapshot. Untracked edits explicitly clear that registration.
    cached_scene_ = current_scene_cache_key_ == 0u ? nullptr : std::move(scene_);
    cached_scene_key_ = cached_scene_ ? current_scene_cache_key_ : 0u;
    current_scene_cache_key_ = 0u;
    scene_ = std::move(scene);
    pending_icon_states_.clear();
    delete_hud_button_ = -1; // WPF will refresh this after updating the selected floor.

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

bool Renderer::TryActivateCachedLevel(std::uint64_t scene_key) noexcept
{
    if (scene_key == 0u)
        return false;

    std::lock_guard lock(scene_mutex_);
    if (!cached_scene_ || cached_scene_key_ != scene_key)
        return false;

    const bool had_scene = static_cast<bool>(scene_);
    std::shared_ptr<LevelScene> previous = std::move(scene_);
    const std::uint64_t previous_key = current_scene_cache_key_;
    scene_ = std::move(cached_scene_);
    current_scene_cache_key_ = scene_key;
    cached_scene_ = previous_key == 0u ? nullptr : std::move(previous);
    cached_scene_key_ = cached_scene_ ? previous_key : 0u;

    // Match SetLevel's activation contract; tab-specific selections, assets
    // and playback timelines are restored by WPF after scene activation.
    pending_icon_states_.clear();
    delete_hud_button_ = -1;
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

void Renderer::RememberCurrentLevel(std::uint64_t scene_key) noexcept
{
    std::lock_guard lock(scene_mutex_);
    current_scene_cache_key_ = scene_key != 0u && scene_ ? scene_key : 0u;
    if (current_scene_cache_key_ == 0u)
    {
        // A full unprepared upload (typically an edit) cannot reuse an
        // inactive scene whose snapshots may be obsolete.
        cached_scene_.reset();
        cached_scene_key_ = 0u;
    }
}

bool Renderer::UpdateFloorIcons(
    std::uint32_t start_floor,
    const EeFloorIconState* states,
    std::uint32_t state_count) noexcept
{
    if (states == nullptr || state_count == 0)
        return false;

    try
    {
        std::lock_guard lock(scene_mutex_);
        if (!scene_ || start_floor > scene_->floors.size() ||
            state_count > scene_->floors.size() - start_floor)
        {
            return false;
        }

        pending_icon_start_floor_ = start_floor;
        pending_icon_states_.assign(states, states + state_count);
        return true;
    }
    catch (...)
    {
        return false;
    }
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

void Renderer::CenterAt(float world_x, float world_y) noexcept
{
    if (!std::isfinite(world_x) || !std::isfinite(world_y))
        return;

    std::lock_guard lock(scene_mutex_);
    camera_x_ = world_x;
    camera_y_ = world_y;
}

bool Renderer::TryGetEditorCamera(float& world_x, float& world_y, float& zoom) noexcept
{
    std::lock_guard lock(scene_mutex_);
    if (!scene_ || !std::isfinite(camera_x_) || !std::isfinite(camera_y_) || !std::isfinite(zoom_))
        return false;

    world_x = camera_x_;
    world_y = camera_y_;
    zoom = zoom_;
    return true;
}

bool Renderer::SetEditorCamera(float world_x, float world_y, float zoom) noexcept
{
    if (!std::isfinite(world_x) || !std::isfinite(world_y) || !std::isfinite(zoom) || zoom <= 0.0f)
        return false;

    std::lock_guard lock(scene_mutex_);
    if (!scene_)
        return false;
    camera_x_ = world_x;
    camera_y_ = world_y;
    zoom_ = std::clamp(zoom, 0.05f, 400.0f);
    return true;
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
    const wchar_t* outline_path,
    const EeSpriteMetadata& image_metadata,
    const EeSpriteMetadata& outline_metadata) noexcept
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
        asset.image_metadata = SpriteMetadata::FromAbi(image_metadata);
        asset.outline_metadata = SpriteMetadata::FromAbi(outline_metadata);
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

bool Renderer::SetFlashTimeline(
    const EeFlashEvent* events,
    std::uint32_t event_count) noexcept
{
    if (event_count > 0u && events == nullptr)
        return false;
    try
    {
        auto next = std::make_shared<std::vector<EeFlashEvent>>();
        if (event_count > 0u)
            next->assign(events, events + event_count);
        std::stable_sort(next->begin(), next->end(),
            [](const EeFlashEvent& a, const EeFlashEvent& b)
            {
                if (a.start_time != b.start_time) return a.start_time < b.start_time;
                return a.source_index < b.source_index;
            });
        std::lock_guard lock(scene_mutex_);
        flash_events_ = std::move(next);
        return true;
    }
    catch (...) { return false; }
}

namespace
{
double FlashOutBounce(double t) noexcept
{
    constexpr double n1=7.5625, d1=2.75;
    if(t<1.0/d1) return n1*t*t;
    if(t<2.0/d1){t-=1.5/d1;return n1*t*t+0.75;}
    if(t<2.5/d1){t-=2.25/d1;return n1*t*t+0.9375;}
    t-=2.625/d1; return n1*t*t+0.984375;
}
double ApplyFlashEase(std::uint32_t ease,double t) noexcept
{
    t=std::clamp(t,0.0,1.0);
    constexpr double pi=3.14159265358979323846,c1=1.70158,c2=c1*1.525,c3=c1+1.0,c4=2.0*pi/3.0,c5=2.0*pi/4.5;
    switch(ease){
    case EE_CAMERA_EASE_IN_SINE:return 1.0-std::cos(t*pi/2.0);
    case EE_CAMERA_EASE_OUT_SINE:return std::sin(t*pi/2.0);
    case EE_CAMERA_EASE_IN_OUT_SINE:return -(std::cos(pi*t)-1.0)/2.0;
    case EE_CAMERA_EASE_IN_QUAD:return t*t;
    case EE_CAMERA_EASE_OUT_QUAD:return 1.0-(1.0-t)*(1.0-t);
    case EE_CAMERA_EASE_IN_OUT_QUAD:return t<0.5?2*t*t:1-std::pow(-2*t+2,2)/2;
    case EE_CAMERA_EASE_IN_CUBIC:return t*t*t;
    case EE_CAMERA_EASE_OUT_CUBIC:return 1-std::pow(1-t,3);
    case EE_CAMERA_EASE_IN_OUT_CUBIC:return t<0.5?4*t*t*t:1-std::pow(-2*t+2,3)/2;
    case EE_CAMERA_EASE_IN_QUART:return t*t*t*t;
    case EE_CAMERA_EASE_OUT_QUART:return 1-std::pow(1-t,4);
    case EE_CAMERA_EASE_IN_OUT_QUART:return t<0.5?8*std::pow(t,4):1-std::pow(-2*t+2,4)/2;
    case EE_CAMERA_EASE_IN_QUINT:return std::pow(t,5);
    case EE_CAMERA_EASE_OUT_QUINT:return 1-std::pow(1-t,5);
    case EE_CAMERA_EASE_IN_OUT_QUINT:return t<0.5?16*std::pow(t,5):1-std::pow(-2*t+2,5)/2;
    case EE_CAMERA_EASE_IN_EXPO:return t<=0?0:std::pow(2,10*t-10);
    case EE_CAMERA_EASE_OUT_EXPO:return t>=1?1:1-std::pow(2,-10*t);
    case EE_CAMERA_EASE_IN_OUT_EXPO:return t<=0?0:t>=1?1:t<0.5?std::pow(2,20*t-10)/2:(2-std::pow(2,-20*t+10))/2;
    case EE_CAMERA_EASE_IN_CIRC:return 1-std::sqrt(std::max(0.0,1-t*t));
    case EE_CAMERA_EASE_OUT_CIRC:return std::sqrt(std::max(0.0,1-std::pow(t-1,2)));
    case EE_CAMERA_EASE_IN_OUT_CIRC:return t<0.5?(1-std::sqrt(std::max(0.0,1-std::pow(2*t,2))))/2:(std::sqrt(std::max(0.0,1-std::pow(-2*t+2,2)))+1)/2;
    case EE_CAMERA_EASE_IN_BACK:return c3*t*t*t-c1*t*t;
    case EE_CAMERA_EASE_OUT_BACK:return 1+c3*std::pow(t-1,3)+c1*std::pow(t-1,2);
    case EE_CAMERA_EASE_IN_OUT_BACK:return t<0.5?std::pow(2*t,2)*((c2+1)*2*t-c2)/2:(std::pow(2*t-2,2)*((c2+1)*(2*t-2)+c2)+2)/2;
    case EE_CAMERA_EASE_IN_ELASTIC:return t<=0?0:t>=1?1:-std::pow(2,10*t-10)*std::sin((t*10-10.75)*c4);
    case EE_CAMERA_EASE_OUT_ELASTIC:return t<=0?0:t>=1?1:std::pow(2,-10*t)*std::sin((t*10-0.75)*c4)+1;
    case EE_CAMERA_EASE_IN_OUT_ELASTIC:return t<=0?0:t>=1?1:t<0.5?-(std::pow(2,20*t-10)*std::sin((20*t-11.125)*c5))/2:std::pow(2,-20*t+10)*std::sin((20*t-11.125)*c5)/2+1;
    case EE_CAMERA_EASE_IN_BOUNCE:return 1-FlashOutBounce(1-t);
    case EE_CAMERA_EASE_OUT_BOUNCE:return FlashOutBounce(t);
    case EE_CAMERA_EASE_IN_OUT_BOUNCE:return t<0.5?(1-FlashOutBounce(1-2*t))/2:(1+FlashOutBounce(2*t-1))/2;
    default:return t;
    }
}
std::uint32_t LerpFlashColor(std::uint32_t a,std::uint32_t b,double t) noexcept
{
    auto ch=[t](std::uint32_t av,std::uint32_t bv){return static_cast<std::uint32_t>(std::clamp(std::lround(static_cast<double>(av)+(static_cast<double>(bv)-av)*t),0l,255l));};
    const auto r=ch((a>>16)&255u,(b>>16)&255u),g=ch((a>>8)&255u,(b>>8)&255u),bl=ch(a&255u,b&255u);
    return (r<<16)|(g<<8)|bl;
}
FlashLayerState ResolveFlashLayer(const EeFlashEvent& e,double time) noexcept
{
    const double raw=e.duration_seconds<=1e-9?1.0:(time-e.start_time)/e.duration_seconds;
    const double p=ApplyFlashEase(e.ease,raw);
    return {LerpFlashColor(e.start_color,e.end_color,p),
        std::clamp(e.start_opacity+(e.end_opacity-e.start_opacity)*static_cast<float>(p),0.0f,1.0f)};
}
}
FlashVisualState ResolveFlashAtTime(const std::vector<EeFlashEvent>* events,double chart_time) noexcept
{
    FlashVisualState result;
    if(events==nullptr) return result;
    for(const EeFlashEvent& e:*events){
        if(e.start_time>chart_time) break;
        const FlashLayerState layer=ResolveFlashLayer(e,chart_time);
        if(e.plane==EE_FLASH_PLANE_FOREGROUND) result.foreground=layer;
        else result.background=layer;
    }
    return result;
}

bool Renderer::SetBloomTimeline(
    const EeBloomEvent* events,
    std::uint32_t event_count) noexcept
{
    if (event_count > 0u && events == nullptr)
        return false;

    try
    {
        auto next = std::make_shared<std::vector<EeBloomEvent>>();
        if (event_count > 0u)
            next->assign(events, events + event_count);
        std::stable_sort(
            next->begin(),
            next->end(),
            [](const EeBloomEvent& left, const EeBloomEvent& right)
            {
                if (left.start_time != right.start_time)
                    return left.start_time < right.start_time;
                return left.source_index < right.source_index;
            });

        std::lock_guard lock(scene_mutex_);
        bloom_events_ = std::move(next);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

namespace
{
std::uint32_t LerpBloomColor(std::uint32_t a, std::uint32_t b, double t) noexcept
{
    const auto channel = [t](std::uint32_t av, std::uint32_t bv)
    {
        const double value = static_cast<double>(av) +
            (static_cast<double>(bv) - static_cast<double>(av)) * t;
        return static_cast<std::uint32_t>(
            std::clamp(std::lround(value), 0l, 255l));
    };
    const std::uint32_t r = channel((a >> 16) & 0xffu, (b >> 16) & 0xffu);
    const std::uint32_t g = channel((a >> 8) & 0xffu, (b >> 8) & 0xffu);
    const std::uint32_t blue = channel(a & 0xffu, b & 0xffu);
    return (r << 16) | (g << 8) | blue;
}
}

BloomVisualState ResolveBloomAtTime(
    const std::vector<EeBloomEvent>* events,
    double chart_time) noexcept
{
    BloomVisualState result;
    if (events == nullptr || events->empty())
        return result;

    const EeBloomEvent* previous = nullptr;
    for (const EeBloomEvent& item : *events)
    {
        if (item.start_time > chart_time)
            break;

        const float start_threshold = previous ? previous->threshold : item.threshold;
        const float start_intensity = previous ? previous->intensity : item.intensity;
        const std::uint32_t start_tint = previous ? previous->tint : item.tint;
        const double raw = item.duration_seconds <= 1e-9
            ? 1.0
            : (chart_time - item.start_time) / item.duration_seconds;
        const double progress = ApplyFlashEase(item.ease, raw);

        result.enabled = item.enabled != 0u;
        result.threshold = start_threshold +
            (item.threshold - start_threshold) * static_cast<float>(progress);
        result.intensity = start_intensity +
            (item.intensity - start_intensity) * static_cast<float>(progress);
        result.tint = LerpBloomColor(start_tint, item.tint, progress);
        previous = &item;
    }
    return result;
}

bool Renderer::SetScreenTileTimeline(
    const EeScreenTileEvent* events,
    std::uint32_t event_count) noexcept
{
    if (event_count > 0u && events == nullptr)
        return false;
    try
    {
        auto next = std::make_shared<std::vector<EeScreenTileEvent>>();
        if (event_count > 0u)
            next->assign(events, events + event_count);
        std::stable_sort(
            next->begin(),
            next->end(),
            [](const EeScreenTileEvent& left, const EeScreenTileEvent& right)
            {
                if (left.start_time != right.start_time)
                    return left.start_time < right.start_time;
                return left.source_index < right.source_index;
            });
        std::lock_guard lock(scene_mutex_);
        screen_tile_events_ = std::move(next);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

ScreenTileVisualState ResolveScreenTileAtTime(
    const std::vector<EeScreenTileEvent>* events,
    double chart_time) noexcept
{
    ScreenTileVisualState result;
    if (events == nullptr)
        return result;

    struct AxisTween
    {
        bool active = false;
        double start_time = 0.0;
        double duration = 0.0;
        float start_value = 1.0f;
        float target_value = 1.0f;
        std::uint32_t ease = EE_CAMERA_EASE_LINEAR;
    };

    AxisTween x_tween;
    AxisTween y_tween;
    auto eval_axis = [](const AxisTween& tween, double time)
    {
        if (!tween.active)
            return tween.target_value;
        const double raw = tween.duration <= 1e-9
            ? 1.0
            : (time - tween.start_time) / tween.duration;
        const double p = ApplyFlashEase(tween.ease, raw);
        return tween.start_value +
            (tween.target_value - tween.start_value) * static_cast<float>(p);
    };

    float x = 1.0f;
    float y = 1.0f;
    bool enabled = false;

    for (const EeScreenTileEvent& item : *events)
    {
        if (item.start_time > chart_time)
            break;

        if (x_tween.active && item.start_time >= x_tween.start_time + x_tween.duration)
        {
            x = x_tween.target_value;
            x_tween.active = false;
        }
        else if (x_tween.active)
        {
            x = eval_axis(x_tween, item.start_time);
        }

        if (y_tween.active && item.start_time >= y_tween.start_time + y_tween.duration)
        {
            y = y_tween.target_value;
            y_tween.active = false;
            if (std::abs(y - 1.0f) <= 1e-6f && std::abs(x - 1.0f) <= 1e-6f)
                enabled = false;
        }
        else if (y_tween.active)
        {
            y = eval_axis(y_tween, item.start_time);
        }

        if (item.duration_seconds <= 1e-9)
        {
            x = item.target_x;
            y = item.target_y;
            enabled = std::abs(x - 1.0f) > 1e-6f || std::abs(y - 1.0f) > 1e-6f;
            continue;
        }

        // Stock Kill(true): finish both previous property tweens before starting
        // the replacement tween from those completed values.
        if (x_tween.active)
        {
            x = x_tween.target_value;
            x_tween.active = false;
        }
        if (y_tween.active)
        {
            y = y_tween.target_value;
            y_tween.active = false;
        }

        if ((item.flags & EE_SCREEN_TILE_ENABLE_BEFORE_TWEEN) != 0u)
            enabled = true;

        x_tween = {true, item.start_time, item.duration_seconds, x, item.target_x, item.ease};
        y_tween = {true, item.start_time, item.duration_seconds, y, item.target_y, item.ease};
    }

    if (x_tween.active)
    {
        if (chart_time >= x_tween.start_time + x_tween.duration)
        {
            x = x_tween.target_value;
            x_tween.active = false;
        }
        else
        {
            x = eval_axis(x_tween, chart_time);
        }
    }

    if (y_tween.active)
    {
        if (chart_time >= y_tween.start_time + y_tween.duration)
        {
            y = y_tween.target_value;
            y_tween.active = false;
            if (std::abs(x - 1.0f) <= 1e-6f && std::abs(y - 1.0f) <= 1e-6f)
                enabled = false;
        }
        else
        {
            y = eval_axis(y_tween, chart_time);
        }
    }

    result.enabled = enabled;
    result.tile_x = x;
    result.tile_y = y;
    return result;
}

bool Renderer::SetBackgroundTimeline(
    const EeBackgroundEvent* events, std::uint32_t event_count) noexcept
{
    if (event_count > 0u && events == nullptr)
        return false;
    try
    {
        auto next = std::make_shared<std::vector<EeBackgroundEvent>>();
        if (event_count > 0u)
            next->assign(events, events + event_count);
        std::stable_sort(next->begin(), next->end(),
            [](const EeBackgroundEvent& a, const EeBackgroundEvent& b) {
                if (a.start_time != b.start_time)
                    return a.start_time < b.start_time;
                return a.source_index < b.source_index;
            });
        std::lock_guard lock(scene_mutex_);
        background_events_ = std::move(next);
        return true;
    }
    catch (...) { return false; }
}

// Background state is resolved from an immutable baseline on every seek.
struct BackgroundRuntime
{
    EeBackgroundState initial{ 0xff14161au, 0xffffffffu, 0u, 0u,
                               100.0f, 100.0f, 100.0f, 0u };
};

using BackgroundAssetCache = std::vector<std::wstring>;

EeBackgroundState ResolveBackgroundAtTime(
    const std::vector<EeBackgroundEvent>* events, double chart_time) noexcept
{
    const BackgroundRuntime runtime{};
    EeBackgroundState state = runtime.initial;
    if (events == nullptr)
        return state;
    for (const auto& event : *events)
    {
        if (event.start_time > chart_time)
            break;
        state = event.state;
    }
    return state;
}

bool Renderer::SetBackgroundAsset(
    std::uint32_t asset_id, const wchar_t* image_path) noexcept
{
    if (image_path == nullptr || asset_id == 0u)
        return false;
    try
    {
        std::lock_guard lock(scene_mutex_);
        auto next = std::make_shared<std::unordered_map<std::uint32_t, std::wstring>>(*background_assets_);
        (*next)[asset_id] = image_path;
        background_assets_ = std::move(next);
        ++background_assets_version_;
        return true;
    }
    catch (...) { return false; }
}

bool Renderer::SetFilterTimeline(
    const EeFilterEvent* events,
    std::uint32_t event_count) noexcept
{
    if (event_count > 0u && events == nullptr)
        return false;
    try
    {
        auto next = std::make_shared<std::vector<EeFilterEvent>>();
        if (event_count > 0u)
            next->assign(events, events + event_count);
        std::stable_sort(
            next->begin(),
            next->end(),
            [](const EeFilterEvent& left, const EeFilterEvent& right)
            {
                if (left.start_time != right.start_time)
                    return left.start_time < right.start_time;
                return left.source_index < right.source_index;
            });
        std::lock_guard lock(scene_mutex_);
        filter_events_ = std::move(next);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

float ApplyFilterIntensity(std::uint32_t filter, float intensity) noexcept
{
    switch (filter)
    {
    case 3u: return 0.212f * intensity;
    case 7u: return std::round(5.0f * intensity);
    case 8u: return 0.5f * intensity;
    case 10u: return 0.9f + 0.1f * intensity;
    case 11u: return 3.0f * intensity;
    case 13u: return 4.0f * intensity;
    case 14u: return 10.0f * intensity;
    case 16u: return 32.0f * intensity;
    case 17u: return 0.375f * intensity;
    case 19u: return 0.04f * intensity - 0.02f;
    case 27u: return 2.0f * intensity;
    case 31u: return 20.0f * intensity;
    case 33u: return 1.0f + intensity;
    case 38u: return 2.4f * intensity;
    default: return intensity;
    }
}

FilterVisualState ResolveFilterStateAtTime(
    const std::vector<EeFilterEvent>* events,
    double chart_time) noexcept
{
    FilterVisualState result;
    if (events == nullptr)
        return result;

    for (const EeFilterEvent& item : *events)
    {
        if (item.start_time > chart_time)
            break;
        if (item.filter >= 41u)
            continue;

        if ((item.flags & EE_FILTER_DISABLE_OTHERS) != 0u)
        {
            for (std::uint32_t i = 0u; i < 41u; ++i)
            {
                if (i != item.filter)
                    result.entries[i].enabled = false;
            }
        }

        const double raw = item.duration_seconds <= 1e-9
            ? 1.0
            : (chart_time - item.start_time) / item.duration_seconds;
        const double progress = ApplyFlashEase(item.ease, raw);
        FilterVisualEntry& entry = result.entries[item.filter];
        entry.intensity = entry.intensity +
            (item.target_intensity - entry.intensity) * static_cast<float>(progress);
        entry.enabled = item.enabled != 0u;
        entry.applied_value = ApplyFilterIntensity(item.filter, entry.intensity);
    }

    return result;
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

ViewportInputTransform Renderer::InputTransformLocked() const noexcept
{
    if (playback_active_ && follow_player_)
        return rendered_input_transform_;
    return {camera_x_, camera_y_, zoom_, 0.0,
        static_cast<double>(width_.load(std::memory_order_relaxed)),
        static_cast<double>(height_.load(std::memory_order_relaxed))};
}

void Renderer::SetPositionTrackDragCallback(EePositionTrackDragCallback callback, void* user_data) noexcept
{
    std::lock_guard lock(scene_mutex_);
    position_drag_callback_ = callback;
    position_drag_user_data_ = user_data;
}

void Renderer::SnapshotPositionTrackDragBaseLocked(
    std::int32_t first_floor,
    std::int32_t last_floor) noexcept
{
    position_drag_base_positions_.clear();
    if (!scene_ || first_floor < 0 || last_floor < first_floor ||
        static_cast<std::size_t>(last_floor) >= scene_->floors.size())
        return;

    position_drag_base_positions_.reserve(static_cast<std::size_t>(last_floor - first_floor + 1));
    for (std::int32_t floor = first_floor; floor <= last_floor; ++floor)
    {
        const EeFloor& item = scene_->floors[static_cast<std::size_t>(floor)];
        position_drag_base_positions_.push_back({item.x, item.y});
    }
}

bool Renderer::IsContiguousFloorSelectionLocked() const noexcept
{
    if (!selected_floors_ || selected_floors_->empty())
        return false;

    return std::adjacent_find(
        selected_floors_->begin(),
        selected_floors_->end(),
        [](std::int32_t left, std::int32_t right) { return right != left + 1; }) ==
        selected_floors_->end();
}

void Renderer::CancelPositionTrackDrag(HWND hwnd) noexcept
{
    if (!position_drag_.Active()) return;
    position_drag_.Cancel();
    {
        std::lock_guard lock(scene_mutex_);
        position_drag_preview_ = {};
        position_drag_base_positions_.clear();
    }
    if (GetCapture() == hwnd) ReleaseCapture();
}

std::int32_t Renderer::SelectFloorAt(int screen_x, int screen_y) noexcept
{
    std::lock_guard lock(scene_mutex_);
    if (!scene_ || zoom_ <= 0.0f)
    {
        selected_floor_ = -1;
        return selected_floor_;
    }

    const auto view = InputTransformLocked();
    const auto world = view.Point(screen_x, screen_y);
    const float world_x = static_cast<float>(world.x);
    const float world_y = static_cast<float>(world.y);
    const float radius = std::max(12.0f / static_cast<float>(view.zoom), 0.856f);

    std::vector<std::uint32_t> candidates;
    scene_->Query(
        world_x - radius,
        world_y - radius,
        world_x + radius,
        world_y + radius,
        candidates);

    std::vector<FloorHitCandidate> hits;
    hits.reserve(candidates.size());
    for (std::uint32_t index : candidates)
    {
        if (index >= scene_->floors.size())
            continue;

        const EeFloor& floor = scene_->floors[index];
        const float dx = floor.x - world_x;
        const float dy = floor.y - world_y;
        const float distance_squared = dx * dx + dy * dy;
        hits.push_back(FloorHitCandidate{
            index,
            distance_squared,
            FloorDrawDepth(index, floor.track_sorting_offset, scene_->floors.size())});
    }

    const std::int32_t best_floor = SelectNearestFloorHit(hits, radius * radius);
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
        CancelPositionTrackDrag(hwnd);
        DisableFollowForManualPan();
        SetFocus(hwnd);
        SetCapture(hwnd);
        panning_ = true;
        pan_button_ = message == WM_MBUTTONDOWN ? WM_MBUTTONUP : WM_RBUTTONUP;
        last_mouse_.x = GET_X_LPARAM(lparam);
        last_mouse_.y = GET_Y_LPARAM(lparam);
        return 0;

    case WM_MOUSEMOVE:
        if (position_drag_.Active())
        {
            position_drag_.Move(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
            {
                std::lock_guard lock(scene_mutex_);
                position_drag_preview_ = position_drag_.Preview();
            }
            return 0;
        }
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
        position_drag_.Cancel();
        {
            std::lock_guard lock(scene_mutex_);
            position_drag_preview_ = {};
            position_drag_base_positions_.clear();
        }
        panning_ = false;
        pan_button_ = 0;
        break;

    case WM_CANCELMODE:
    case WM_KILLFOCUS:
        CancelPositionTrackDrag(hwnd);
        if (panning_)
        {
            panning_ = false;
            pan_button_ = 0;
            if (GetCapture() == hwnd) ReleaseCapture();
        }
        break;

    case WM_KEYDOWN:
    case WM_SYSKEYDOWN:
        if (wparam == VK_ESCAPE && position_drag_.Active())
        {
            CancelPositionTrackDrag(hwnd);
            return 0;
        }
        break;

    case WM_LBUTTONUP:
        if (position_drag_.Active())
        {
            const auto drag = position_drag_.End(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
            {
                std::lock_guard lock(scene_mutex_);
                position_drag_preview_ = {};
                position_drag_base_positions_.clear();
            }
            if (GetCapture() == hwnd) ReleaseCapture();
            if (!drag.moved)
                NotifySelectionChanged(drag.first_floor, position_drag_modifiers_);
            else if (drag.delta.x != 0.0 || drag.delta.y != 0.0)
            {
                EePositionTrackDragCallback callback;
                void* user_data;
                {
                    std::lock_guard lock(scene_mutex_);
                    callback = position_drag_callback_;
                    user_data = position_drag_user_data_;
                }
                if (callback != nullptr)
                    callback(
                        user_data,
                        drag.first_floor,
                        drag.last_floor,
                        drag.delta.x,
                        drag.delta.y);
            }
            return 0;
        }
        break;

    case WM_LBUTTONDOWN:
    {
        if (panning_) return 0;
        const std::uint32_t modifiers = CaptureInputModifiers(wparam);
        SetFocus(hwnd);
        const std::int32_t selected = SelectFloorAt(GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
        if ((modifiers & EE_INPUT_MODIFIER_ALT) != 0 && selected >= 0)
        {
            ViewportInputTransform view;
            std::int32_t first_floor = selected;
            std::int32_t last_floor = selected;
            {
                std::lock_guard lock(scene_mutex_);
                view = InputTransformLocked();
                if (selected_floors_ &&
                    std::binary_search(selected_floors_->begin(), selected_floors_->end(), selected))
                {
                    if (!IsContiguousFloorSelectionLocked())
                        return 0;
                    first_floor = selected_floors_->front();
                    last_floor = selected_floors_->back();
                }
                SnapshotPositionTrackDragBaseLocked(first_floor, last_floor);
            }
            SetCapture(hwnd);
            position_drag_modifiers_ = modifiers;
            position_drag_.Begin(
                first_floor,
                last_floor,
                GET_X_LPARAM(lparam),
                GET_Y_LPARAM(lparam),
                GetSystemMetrics(SM_CXDRAG),
                GetSystemMetrics(SM_CYDRAG),
                view);
            {
                std::lock_guard lock(scene_mutex_);
                position_drag_preview_ = position_drag_.Preview();
            }
            return 0;
        }
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
        std::shared_ptr<const std::vector<EeFlashEvent>> flash_events;
        std::shared_ptr<const std::vector<EeBloomEvent>> bloom_events;
        std::shared_ptr<const std::vector<EeScreenTileEvent>> screen_tile_events;
        std::shared_ptr<const std::vector<EeFilterEvent>> filter_events;
        std::shared_ptr<const std::vector<EeBackgroundEvent>> background_events;
        std::shared_ptr<const std::unordered_map<std::uint32_t, std::wstring>> background_assets;
        std::uint64_t background_assets_version = 0;
        float camera_x = 0.0f;
        float camera_y = 0.0f;
        float zoom = 28.0f;
        std::shared_ptr<const std::vector<std::int32_t>> selected_floors;
        std::int32_t selected_floor = -1;
        bool clean_view = false;
        bool show_floor_numbers = false;
        int editor_hud_hover = -1;
        int delete_hud_button = -1;
        PositionTrackDragPreview position_drag_preview;
        std::vector<InputWorldPoint> position_drag_base_positions;
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
            if (scene_ && !pending_icon_states_.empty())
            {
                for (std::size_t i = 0; i < pending_icon_states_.size(); ++i)
                {
                    EeFloor& floor = scene_->floors[pending_icon_start_floor_ + i];
                    const EeFloorIconState& state = pending_icon_states_[i];
                    floor.icon_id = state.icon_id;
                    floor.icon_flags = state.icon_flags;
                    floor.icon_angle = state.icon_angle;
                }
                pending_icon_states_.clear();
                ++scene_version_;
            }
            scene = scene_;
            icon_assets = icon_assets_;
            playback_timings = playback_timings_;
            camera_events = camera_events_;
            flash_events = flash_events_;
            bloom_events = bloom_events_;
            screen_tile_events = screen_tile_events_;
            filter_events = filter_events_;
            background_events = background_events_;
            background_assets = background_assets_;
            background_assets_version = background_assets_version_;
            camera_x = camera_x_;
            camera_y = camera_y_;
            zoom = zoom_;
            selected_floors = selected_floors_;
            selected_floor = selected_floor_;
            clean_view = clean_view_;
            show_floor_numbers = show_floor_numbers_;
            editor_hud_hover = hover_hud_button_;
            delete_hud_button = delete_hud_button_;
            position_drag_preview = position_drag_preview_;
            position_drag_base_positions = position_drag_base_positions_;
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
            {
                scene->UpdateTrackTransforms();
                scene->UpdateTrackVisuals();

                if (position_drag_preview.active &&
                    position_drag_preview.first_floor >= 0 &&
                    position_drag_preview.last_floor >= position_drag_preview.first_floor &&
                    position_drag_base_positions.size() ==
                        static_cast<std::size_t>(
                            position_drag_preview.last_floor -
                            position_drag_preview.first_floor + 1))
                {
                    for (std::int32_t floor = position_drag_preview.first_floor;
                         floor <= position_drag_preview.last_floor;
                         ++floor)
                    {
                        if (static_cast<std::size_t>(floor) >= scene->floors.size())
                            break;
                        const std::size_t local = static_cast<std::size_t>(
                            floor - position_drag_preview.first_floor);
                        scene->floors[static_cast<std::size_t>(floor)].x =
                            static_cast<float>(position_drag_base_positions[local].x) +
                            position_drag_preview.offset_x;
                        scene->floors[static_cast<std::size_t>(floor)].y =
                            static_cast<float>(position_drag_base_positions[local].y) +
                            position_drag_preview.offset_y;
                    }
                }
            }

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

            {
                std::lock_guard lock(scene_mutex_);
                rendered_input_transform_ = {render_camera_x, render_camera_y, render_zoom,
                    render_camera_rotation,
                    static_cast<double>(width_.load(std::memory_order_relaxed)),
                    static_cast<double>(height_.load(std::memory_order_relaxed))};
            }
            backend.SetFlashState(ResolveFlashAtTime(flash_events.get(), chart_time));
            backend.SetBloomState(ResolveBloomAtTime(bloom_events.get(), chart_time));
            backend.SetScreenTileState(ResolveScreenTileAtTime(screen_tile_events.get(), chart_time));
            backend.SetFilterState(ResolveFilterStateAtTime(filter_events.get(), chart_time));
            backend.SetBackgroundState(ResolveBackgroundAtTime(background_events.get(), chart_time));
            backend.SyncBackgroundAssets(background_assets.get(), background_assets_version);
            backend.SetShowFloorNumbers(show_floor_numbers && !clean_view && !playback_active);
            backend.SetEditorHudHover(clean_view || playback_active ? -1 : editor_hud_hover);
            backend.SetEditorHudDeleteButton(clean_view || playback_active ? -1 : delete_hud_button);
            const auto render_started = std::chrono::steady_clock::now();
            static const std::vector<std::int32_t> no_selected_floors;
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
                clean_view ? no_selected_floors : *selected_floors,
                clean_view ? -1 : selected_floor,
                playback);

            if (scene != nullptr && position_drag_preview.active &&
                position_drag_preview.first_floor >= 0 &&
                position_drag_preview.last_floor >= position_drag_preview.first_floor &&
                position_drag_base_positions.size() ==
                    static_cast<std::size_t>(
                        position_drag_preview.last_floor -
                        position_drag_preview.first_floor + 1))
            {
                for (std::int32_t floor = position_drag_preview.first_floor;
                     floor <= position_drag_preview.last_floor;
                     ++floor)
                {
                    if (static_cast<std::size_t>(floor) >= scene->floors.size())
                        break;
                    const std::size_t local = static_cast<std::size_t>(
                        floor - position_drag_preview.first_floor);
                    scene->floors[static_cast<std::size_t>(floor)].x =
                        static_cast<float>(position_drag_base_positions[local].x);
                    scene->floors[static_cast<std::size_t>(floor)].y =
                        static_cast<float>(position_drag_base_positions[local].y);
                }
            }

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

    ShutdownDiagnosticLogOnce(
        ShutdownDiagnosticPhase::RenderLoopStopObserved,
        "RenderLoop.stopObserved");
    ShutdownD2DDiagnosticsActive = true;
    backend.Shutdown();
    ShutdownD2DDiagnosticsActive = false;
    ShutdownDiagnosticLogOnce(
        ShutdownDiagnosticPhase::RenderLoopCoUninitializeEntry,
        "RenderLoop.coUninitialize.begin");
    if (com_initialized)
        CoUninitialize();
    ShutdownDiagnosticLogOnce(
        ShutdownDiagnosticPhase::RenderLoopCoUninitializeExit,
        "RenderLoop.coUninitialize.end");
}

void Renderer::StopRenderThread(bool shutdown_diagnostic) noexcept
{
    stop_requested_.store(true, std::memory_order_release);
    if (shutdown_diagnostic)
    {
        ShutdownDiagnosticLogOnce(
            ShutdownDiagnosticPhase::StopFlagSet,
            "StopRenderThread.stopFlagSet");
    }
    if (render_thread_.joinable())
    {
        if (shutdown_diagnostic)
        {
            ShutdownDiagnosticLogOnce(
                ShutdownDiagnosticPhase::RenderThreadJoinEntry,
                "StopRenderThread.join.begin");
        }
        render_thread_.join();
        if (shutdown_diagnostic)
        {
            ShutdownDiagnosticLogOnce(
                ShutdownDiagnosticPhase::RenderThreadJoinExit,
                "StopRenderThread.join.end");
        }
    }
    if (shutdown_diagnostic)
    {
        ShutdownDiagnosticLogOnce(
            ShutdownDiagnosticPhase::ChildWindowDestroyEntry,
            "StopRenderThread.childWindowDestroy.begin");
    }
    window_.Destroy();
    if (shutdown_diagnostic)
    {
        ShutdownDiagnosticLogOnce(
            ShutdownDiagnosticPhase::ChildWindowDestroyExit,
            "StopRenderThread.childWindowDestroy.end");
    }
}
}
