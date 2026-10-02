#pragma once

#include "d2d_backend.h"
#include "level_scene.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace ee
{
inline double CalculateFollowDuration(
    const std::vector<EePlaybackTiming>* timings,
    std::size_t floor) noexcept
{
    constexpr double Pi = 3.14159265358979323846;
    if (timings == nullptr || floor >= timings->size())
        return 0.0;

    const EePlaybackTiming& timing = (*timings)[floor];
    const double rotation_duration = std::max(
        0.0,
        timing.exit_time - timing.entry_time - timing.pause_seconds);
    const double moved = std::abs(static_cast<double>(timing.angle_moved));
    if (rotation_duration > 1e-9 && moved > 1e-6)
    {
        const double beat_seconds = rotation_duration * Pi / moved;
        return std::max(0.0, beat_seconds * 2.0);
    }

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
    std::int32_t follow_target_floor = -1;
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
        follow_target_floor = -1;
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

    void InitializeFromStart(
        const LevelScene* scene,
        const std::vector<EePlaybackTiming>* timings,
        std::uint64_t version) noexcept
    {
        initialized = true;
        floor = 0;
        follow_target_floor = 0;
        x = scene->floors[0].x;
        y = scene->floors[0].y;
        from_x = x;
        from_y = y;
        to_x = x;
        to_y = y;
        start_time = (*timings)[0].entry_time;
        duration = 0.0;
        last_chart_time = start_time;
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
            InitializeFromStart(scene, timings, version);
        }

        const std::int32_t target_floor = playback.floor;
        if (target_floor > floor)
        {
            for (std::int32_t next_floor = floor + 1; next_floor <= target_floor;)
            {
                const std::size_t index = static_cast<std::size_t>(next_floor);
                if (index >= scene->floors.size() || index >= timings->size())
                    break;

                const double transition_time = (*timings)[index].entry_time;
                std::int32_t equal_time_end = next_floor;
                while (equal_time_end < target_floor)
                {
                    const std::size_t following = static_cast<std::size_t>(equal_time_end + 1);
                    if (following >= timings->size() ||
                        std::abs((*timings)[following].entry_time - transition_time) > 1e-9)
                    {
                        break;
                    }
                    ++equal_time_end;
                }

                Evaluate(transition_time);

                from_x = x;
                from_y = y;
                to_x = scene->floors[index].x;
                to_y = scene->floors[index].y;
                start_time = transition_time;
                duration = CalculateFollowDuration(
                    timings,
                    static_cast<std::size_t>(equal_time_end));
                follow_target_floor = next_floor;
                floor = equal_time_end;
                next_floor = equal_time_end + 1;
            }
        }

        if (target_floor == floor &&
            follow_target_floor >= 0 &&
            static_cast<std::size_t>(follow_target_floor) < scene->floors.size())
        {
            // Keep the transition origin fixed, but follow the chosen planet's
            // current transform when MoveTrack moves its destination.
            const EeFloor& target = scene->floors[static_cast<std::size_t>(follow_target_floor)];
            to_x = target.x;
            to_y = target.y;
        }

        Evaluate(chart_time);
        last_chart_time = chart_time;
        scene_version = version;
    }
};
}
