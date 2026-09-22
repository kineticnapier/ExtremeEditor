#include "track_transform_runtime.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <vector>

namespace
{
double Measure(std::uint32_t floor_count, int iterations)
{
    std::vector<EeFloor> floors(floor_count);
    for (std::uint32_t i = 0; i < floor_count; ++i)
    {
        floors[i].x = static_cast<float>(i);
        floors[i].y = 0.0f;
    }

    std::vector<EeTrackTransformEvent> events(floor_count);
    for (std::uint32_t i = 0; i < floor_count; ++i)
    {
        EeTrackTransformEvent item{};
        item.floor = static_cast<std::int32_t>(i);
        item.start_time = 1000.0 + static_cast<double>(i) * 0.001;
        item.duration_seconds = 1.0;
        item.flags = EE_TRACK_TRANSFORM_X;
        item.start_x = static_cast<float>(i);
        item.target_x = static_cast<float>(i + 1u);
        events[i] = item;
    }

    ee::TrackTransformRuntime runtime;
    runtime.ResetBase(floors);
    if (!runtime.SetTimeline(events.data(), static_cast<std::uint32_t>(events.size())))
    {
        std::cerr << "FAIL: track-transform timeline setup rejected valid input.\n";
        std::exit(1);
    }

    runtime.SetPlaybackAnchor(0.0, 1.0, EE_PLAYBACK_FLAG_ACTIVE);
    ee::TrackTransformRuntime::CellMap cells;

    // Warm up the Update path before measuring.
    runtime.Update(floors, cells);

    const auto started = std::chrono::steady_clock::now();
    for (int i = 0; i < iterations; ++i)
        runtime.Update(floors, cells);
    const auto finished = std::chrono::steady_clock::now();

    return std::chrono::duration<double, std::milli>(finished - started).count();
}
}

int main()
{
    constexpr std::uint32_t SmallFloorCount = 5000u;
    constexpr std::uint32_t LargeFloorCount = 40000u;
    constexpr int Iterations = 200;
    constexpr double MaxScaleRatio = 4.0;

    // All events start far in the future. At chart time 0 there are no active
    // track transforms, so per-frame work should not scale with total future
    // track count.
    (void)Measure(1000u, 20);

    const double small_ms = Measure(SmallFloorCount, Iterations);
    const double large_ms = Measure(LargeFloorCount, Iterations);
    const double ratio = large_ms / std::max(0.001, small_ms);

    std::cout << "track-transform Update " << SmallFloorCount
              << " future-only tracks: " << small_ms << " ms\n";
    std::cout << "track-transform Update " << LargeFloorCount
              << " future-only tracks: " << large_ms << " ms\n";
    std::cout << "scale ratio for 8x total future tracks: " << ratio << "x\n";

    if (ratio > MaxScaleRatio)
    {
        std::cerr
            << "FAIL: RED: TrackTransformRuntime::Update scans total future tracks every frame. "
            << "8x future-only track count took " << ratio
            << "x time; limit=" << MaxScaleRatio << "x.\n";
        return 1;
    }

    std::cout << "PASS: future-only track count does not dominate TrackTransformRuntime::Update.\n";
    return 0;
}
