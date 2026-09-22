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

float EventProgress(const EeCameraEvent& item, double chart_time) noexcept
{
    const double progress = item.duration_seconds <= 1e-9
        ? 1.0
        : std::clamp((chart_time - item.start_time) / item.duration_seconds, 0.0, 1.0);
    return static_cast<float>(ApplyEase(item.ease, progress));
}

float EvaluateAxis(
    const LevelScene* scene,
    const EeCameraEvent& item,
    double chart_time,
    float player,
    bool x_axis) noexcept
{
    float start = x_axis ? item.start_x : item.start_y;
    float target = x_axis ? item.target_x : item.target_y;

    if ((item.reference_flags & EE_CAMERA_REFERENCE_TILE) != 0u &&
        scene != nullptr &&
        item.reference_floor >= 0 &&
        static_cast<std::size_t>(item.reference_floor) < scene->floors.size())
    {
        const EeFloor& floor = scene->floors[static_cast<std::size_t>(item.reference_floor)];
        const float base = x_axis ? floor.x : floor.y;
        start += base;
        target += base;
    }
    else
    {
        const std::uint32_t player_flag = x_axis
            ? EE_CAMERA_TARGET_PLAYER_X
            : EE_CAMERA_TARGET_PLAYER_Y;
        if ((item.flags & player_flag) != 0u)
        {
            // Preserve the existing Player-relative behaviour. Tile references
            // are independent and resolve from the transformed LevelScene above.
            target += player;
        }
    }

    return Lerp(start, target, EventProgress(item, chart_time));
}

float EvaluateScalar(
    const EeCameraEvent& item,
    double chart_time,
    bool zoom) noexcept
{
    const float start = zoom ? item.start_zoom : item.start_rotation;
    const float target = zoom ? item.target_zoom : item.target_rotation;
    return Lerp(start, target, EventProgress(item, chart_time));
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
    const LevelScene* scene,
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

    const EeCameraEvent* x_event = nullptr;
    const EeCameraEvent* y_event = nullptr;
    const EeCameraEvent* rotation_event = nullptr;
    const EeCameraEvent* zoom_event = nullptr;

    auto it = upper;
    while (it != events->begin() &&
           (x_event == nullptr || y_event == nullptr ||
            rotation_event == nullptr || zoom_event == nullptr))
    {
        --it;
        const EeCameraEvent& item = *it;
        if (x_event == nullptr && (item.flags & EE_CAMERA_APPLY_X) != 0u)
            x_event = &item;
        if (y_event == nullptr && (item.flags & EE_CAMERA_APPLY_Y) != 0u)
            y_event = &item;
        if (rotation_event == nullptr && (item.flags & EE_CAMERA_APPLY_ROTATION) != 0u)
            rotation_event = &item;
        if (zoom_event == nullptr && (item.flags & EE_CAMERA_APPLY_ZOOM) != 0u)
            zoom_event = &item;
    }

    if (x_event != nullptr)
        result.x = EvaluateAxis(scene, *x_event, chart_time, playback.stationary_x, true);
    if (y_event != nullptr)
        result.y = EvaluateAxis(scene, *y_event, chart_time, playback.stationary_y, false);
    if (rotation_event != nullptr)
        result.rotation = EvaluateScalar(*rotation_event, chart_time, false);
    if (zoom_event != nullptr)
    {
        result.zoom_multiplier = std::clamp(
            EvaluateScalar(*zoom_event, chart_time, true),
            0.01f,
            100.0f);
    }

    return result;
}
