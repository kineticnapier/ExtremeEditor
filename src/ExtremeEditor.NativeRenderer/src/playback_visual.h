#pragma once

#include "d2d_backend.h"
#include "level_scene.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace ee
{
inline PlaybackVisualState CalculatePlaybackVisual(
    const LevelScene* scene,
    const std::vector<EePlaybackTiming>* timings,
    double chart_time) noexcept
{
    constexpr float DefaultPlanetDistance = 1.5f;

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
    const double direction =
        (timing.flags & EE_PLAYBACK_TIMING_FLAG_CCW) != 0u ? -1.0 : 1.0;
    const double angle = static_cast<double>(timing.entry_angle) +
        direction * static_cast<double>(timing.angle_moved) * progress;

    float orbit_distance = DefaultPlanetDistance;
    if (floor + 1u < scene->floors.size())
    {
        const EeFloor& next_floor = scene->floors[floor + 1u];
        const float dx = next_floor.x - stationary.x;
        const float dy = next_floor.y - stationary.y;
        const float resolved_distance = std::hypot(dx, dy);
        if (std::isfinite(resolved_distance) && resolved_distance > 0.000001f)
            orbit_distance = resolved_distance;
    }

    result.active = true;
    result.floor = static_cast<std::int32_t>(floor);
    result.stationary_x = stationary.x;
    result.stationary_y = stationary.y;
    result.orbiting_x =
        stationary.x + static_cast<float>(std::sin(angle)) * orbit_distance;
    result.orbiting_y =
        stationary.y + static_cast<float>(std::cos(angle)) * orbit_distance;
    result.stationary_is_red = (floor & 1u) == 0u;
    return result;
}
}
