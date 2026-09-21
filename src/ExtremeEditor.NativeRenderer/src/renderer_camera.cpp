#include "renderer.h"
#include "renderer_camera.h"

#include <algorithm>
#include <cmath>

namespace ee
{
namespace
{
float Lerp(float a, float b, float t) noexcept
{
    return a + (b - a) * t;
}

double OutBounce(double t) noexcept
{
    constexpr double n1 = 7.5625;
    constexpr double d1 = 2.75;
    if (t < 1.0 / d1)
        return n1 * t * t;
    if (t < 2.0 / d1)
    {
        t -= 1.5 / d1;
        return n1 * t * t + 0.75;
    }
    if (t < 2.5 / d1)
    {
        t -= 2.25 / d1;
        return n1 * t * t + 0.9375;
    }
    t -= 2.625 / d1;
    return n1 * t * t + 0.984375;
}

double ApplyEase(std::uint32_t ease, double t) noexcept
{
    t = std::clamp(t, 0.0, 1.0);
    constexpr double pi = 3.14159265358979323846;
    constexpr double c1 = 1.70158;
    constexpr double c2 = c1 * 1.525;
    constexpr double c3 = c1 + 1.0;
    constexpr double c4 = 2.0 * pi / 3.0;
    constexpr double c5 = 2.0 * pi / 4.5;

    switch (ease)
    {
    case EE_CAMERA_EASE_IN_SINE:
        return 1.0 - std::cos(t * pi / 2.0);
    case EE_CAMERA_EASE_OUT_SINE:
        return std::sin(t * pi / 2.0);
    case EE_CAMERA_EASE_IN_OUT_SINE:
        return -(std::cos(pi * t) - 1.0) / 2.0;
    case EE_CAMERA_EASE_IN_QUAD:
        return t * t;
    case EE_CAMERA_EASE_OUT_QUAD:
        return 1.0 - (1.0 - t) * (1.0 - t);
    case EE_CAMERA_EASE_IN_OUT_QUAD:
        return t < 0.5 ? 2.0 * t * t : 1.0 - std::pow(-2.0 * t + 2.0, 2.0) / 2.0;
    case EE_CAMERA_EASE_IN_CUBIC:
        return t * t * t;
    case EE_CAMERA_EASE_OUT_CUBIC:
        return 1.0 - std::pow(1.0 - t, 3.0);
    case EE_CAMERA_EASE_IN_OUT_CUBIC:
        return t < 0.5 ? 4.0 * t * t * t : 1.0 - std::pow(-2.0 * t + 2.0, 3.0) / 2.0;
    case EE_CAMERA_EASE_IN_QUART:
        return t * t * t * t;
    case EE_CAMERA_EASE_OUT_QUART:
        return 1.0 - std::pow(1.0 - t, 4.0);
    case EE_CAMERA_EASE_IN_OUT_QUART:
        return t < 0.5 ? 8.0 * std::pow(t, 4.0) : 1.0 - std::pow(-2.0 * t + 2.0, 4.0) / 2.0;
    case EE_CAMERA_EASE_IN_QUINT:
        return std::pow(t, 5.0);
    case EE_CAMERA_EASE_OUT_QUINT:
        return 1.0 - std::pow(1.0 - t, 5.0);
    case EE_CAMERA_EASE_IN_OUT_QUINT:
        return t < 0.5 ? 16.0 * std::pow(t, 5.0) : 1.0 - std::pow(-2.0 * t + 2.0, 5.0) / 2.0;
    case EE_CAMERA_EASE_IN_EXPO:
        return t <= 0.0 ? 0.0 : std::pow(2.0, 10.0 * t - 10.0);
    case EE_CAMERA_EASE_OUT_EXPO:
        return t >= 1.0 ? 1.0 : 1.0 - std::pow(2.0, -10.0 * t);
    case EE_CAMERA_EASE_IN_OUT_EXPO:
        return t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : t < 0.5
            ? std::pow(2.0, 20.0 * t - 10.0) / 2.0
            : (2.0 - std::pow(2.0, -20.0 * t + 10.0)) / 2.0;
    case EE_CAMERA_EASE_IN_CIRC:
        return 1.0 - std::sqrt(std::max(0.0, 1.0 - t * t));
    case EE_CAMERA_EASE_OUT_CIRC:
        return std::sqrt(std::max(0.0, 1.0 - std::pow(t - 1.0, 2.0)));
    case EE_CAMERA_EASE_IN_OUT_CIRC:
        return t < 0.5
            ? (1.0 - std::sqrt(std::max(0.0, 1.0 - std::pow(2.0 * t, 2.0)))) / 2.0
            : (std::sqrt(std::max(0.0, 1.0 - std::pow(-2.0 * t + 2.0, 2.0))) + 1.0) / 2.0;
    case EE_CAMERA_EASE_IN_BACK:
        return c3 * t * t * t - c1 * t * t;
    case EE_CAMERA_EASE_OUT_BACK:
        return 1.0 + c3 * std::pow(t - 1.0, 3.0) + c1 * std::pow(t - 1.0, 2.0);
    case EE_CAMERA_EASE_IN_OUT_BACK:
        return t < 0.5
            ? std::pow(2.0 * t, 2.0) * ((c2 + 1.0) * 2.0 * t - c2) / 2.0
            : (std::pow(2.0 * t - 2.0, 2.0) * ((c2 + 1.0) * (2.0 * t - 2.0) + c2) + 2.0) / 2.0;
    case EE_CAMERA_EASE_IN_ELASTIC:
        return t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0
            : -std::pow(2.0, 10.0 * t - 10.0) * std::sin((t * 10.0 - 10.75) * c4);
    case EE_CAMERA_EASE_OUT_ELASTIC:
        return t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0
            : std::pow(2.0, -10.0 * t) * std::sin((t * 10.0 - 0.75) * c4) + 1.0;
    case EE_CAMERA_EASE_IN_OUT_ELASTIC:
        return t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : t < 0.5
            ? -(std::pow(2.0, 20.0 * t - 10.0) * std::sin((20.0 * t - 11.125) * c5)) / 2.0
            : std::pow(2.0, -20.0 * t + 10.0) * std::sin((20.0 * t - 11.125) * c5) / 2.0 + 1.0;
    case EE_CAMERA_EASE_IN_BOUNCE:
        return 1.0 - OutBounce(1.0 - t);
    case EE_CAMERA_EASE_OUT_BOUNCE:
        return OutBounce(t);
    case EE_CAMERA_EASE_IN_OUT_BOUNCE:
        return t < 0.5
            ? (1.0 - OutBounce(1.0 - 2.0 * t)) / 2.0
            : (1.0 + OutBounce(2.0 * t - 1.0)) / 2.0;
    default:
        return t;
    }
}
}

bool Renderer::SetCameraTimeline(const EeCameraEvent* events, std::uint32_t event_count) noexcept
{
    if (event_count > 0 && events == nullptr)
        return false;

    try
    {
        auto next = std::make_shared<std::vector<EeCameraEvent>>();
        if (event_count > 0)
            next->assign(events, events + event_count);

        std::lock_guard lock(scene_mutex_);
        camera_events_ = std::move(next);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

CameraVisualState CalculateCameraVisual(
    const std::vector<EeCameraEvent>* events,
    const PlaybackVisualState& playback,
    double chart_time) noexcept
{
    CameraVisualState result;
    if (!playback.active)
        return result;

    result.active = true;
    result.x = playback.stationary_x;
    result.y = playback.stationary_y;
    result.zoom_multiplier = 1.0f;
    result.rotation = 0.0f;

    if (events == nullptr || events->empty())
        return result;

    const auto upper = std::upper_bound(
        events->begin(),
        events->end(),
        chart_time,
        [](double value, const EeCameraEvent& item)
        {
            return value < item.start_time;
        });
    if (upper == events->begin())
        return result;

    const EeCameraEvent& item = *(upper - 1);
    const float target_x = (item.flags & EE_CAMERA_TARGET_PLAYER_X) != 0u
        ? playback.stationary_x + item.target_x
        : item.target_x;
    const float target_y = (item.flags & EE_CAMERA_TARGET_PLAYER_Y) != 0u
        ? playback.stationary_y + item.target_y
        : item.target_y;

    const double progress = item.duration_seconds <= 1e-9
        ? 1.0
        : std::clamp((chart_time - item.start_time) / item.duration_seconds, 0.0, 1.0);
    const float t = static_cast<float>(ApplyEase(item.ease, progress));

    result.x = Lerp(item.start_x, target_x, t);
    result.y = Lerp(item.start_y, target_y, t);
    result.rotation = Lerp(item.start_rotation, item.target_rotation, t);
    result.zoom_multiplier = std::clamp(Lerp(item.start_zoom, item.target_zoom, t), 0.01f, 100.0f);
    return result;
}
}
