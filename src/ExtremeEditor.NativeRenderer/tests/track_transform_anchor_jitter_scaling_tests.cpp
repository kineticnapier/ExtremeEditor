#include "level_scene.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <vector>

namespace
{
std::shared_ptr<ee::LevelScene> CreateScene(std::uint32_t floor_count)
{
    std::vector<EeFloor> floors(floor_count);
    for (std::uint32_t i = 0; i < floor_count; ++i)
    {
        EeFloor floor{};
        floor.x = static_cast<float>(i);
        floor.y = 0.0f;
        floor.geometry_id = 0u;
        floor.icon_id = EE_ICON_NONE;
        floor.transform_scale_x = 1.0f;
        floor.transform_scale_y = 1.0f;
        floor.transform_opacity = 1.0f;
        floors[i] = floor;
    }

    const EeGeometry geometry{0u, 3u};
    const EePoint points[] = {
        {-0.5f, -0.5f},
        {0.5f, -0.5f},
        {0.0f, 0.5f}
    };

    std::shared_ptr<ee::LevelScene> scene = ee::LevelScene::Create(
        floors.data(),
        floor_count,
        &geometry,
        1u,
        points,
        3u,
        0.0f,
        -1.0f,
        static_cast<float>(floor_count),
        1.0f);
    if (!scene)
    {
        std::cerr << "FAIL: could not create LevelScene.\n";
        std::exit(1);
    }

    std::vector<EeTrackTransformEvent> events(floor_count);
    for (std::uint32_t i = 0; i < floor_count; ++i)
    {
        EeTrackTransformEvent item{};
        item.floor = static_cast<std::int32_t>(i);
        item.start_time = static_cast<double>(i % 1000u);
        item.duration_seconds = 0.25;
        item.flags = EE_TRACK_TRANSFORM_X;
        item.start_x = static_cast<float>(i);
        item.target_x = static_cast<float>(i); // avoid CellMap churn in this regression
        events[i] = item;
    }

    if (!scene->SetTrackTransformTimeline(
            events.data(),
            static_cast<std::uint32_t>(events.size())))
    {
        std::cerr << "FAIL: track-transform timeline setup rejected valid input.\n";
        std::exit(1);
    }

    return scene;
}

double Measure(std::uint32_t floor_count)
{
    std::shared_ptr<ee::LevelScene> scene = CreateScene(floor_count);

    // Everything is already settled here. The UI may refresh its playback anchor
    // a few milliseconds behind the render thread's last sampled time. That clock
    // jitter must not be interpreted as an editor seek that rebuilds every track.
    scene->SetTrackPlaybackAnchor(2000.0, 1.0, EE_PLAYBACK_FLAG_ACTIVE);
    scene->UpdateTrackTransforms();

    constexpr int Iterations = 6;
    constexpr double JitterSeconds = 0.002;
    const auto started = std::chrono::steady_clock::now();
    for (int i = 0; i < Iterations; ++i)
    {
        const double chart_time = (i & 1) == 0
            ? 2000.0 - JitterSeconds
            : 2000.0;
        scene->SetTrackPlaybackAnchor(chart_time, 1.0, EE_PLAYBACK_FLAG_ACTIVE);
        scene->UpdateTrackTransforms();
    }
    const auto finished = std::chrono::steady_clock::now();

    return std::chrono::duration<double, std::milli>(finished - started).count() /
           static_cast<double>(Iterations);
}
}

int main()
{
    constexpr std::uint32_t SmallFloorCount = 5000u;
    constexpr std::uint32_t LargeFloorCount = 40000u;
    constexpr double MaxScaleRatio = 4.0;

    (void)Measure(1000u);

    const double small_ms = Measure(SmallFloorCount);
    const double large_ms = Measure(LargeFloorCount);
    const double ratio = large_ms / std::max(0.001, small_ms);

    std::cout << "track-transform anchor jitter " << SmallFloorCount
              << " settled tracks: " << small_ms << " ms\n";
    std::cout << "track-transform anchor jitter " << LargeFloorCount
              << " settled tracks: " << large_ms << " ms\n";
    std::cout << "scale ratio for 8x settled tracks: " << ratio << "x\n";

    if (ratio > MaxScaleRatio)
    {
        std::cerr
            << "FAIL: RED: tiny playback-anchor jitter triggers total track rebuilds. "
            << "8x settled tracks took " << ratio << "x time; limit=" << MaxScaleRatio << "x.\n";
        return 1;
    }

    std::cout << "PASS: playback-anchor jitter does not rebuild settled track history.\n";
    return 0;
}
