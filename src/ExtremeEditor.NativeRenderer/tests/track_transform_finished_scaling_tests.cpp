#include "track_transform_runtime.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <vector>

namespace
{
double Measure(std::uint32_t floor_count)
{
    std::vector<EeFloor> floors(floor_count);
    ee::TrackTransformRuntime::CellMap cells;
    cells.reserve(floor_count / 8u + 1u);

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
        item.start_time = static_cast<double>(i % 1000u);
        item.duration_seconds = 0.25;
        item.flags = EE_TRACK_TRANSFORM_X;
        item.start_x = static_cast<float>(i);
        item.target_x = static_cast<float>(i); // keep CellMap work out of this scaling test
        events[i] = item;
    }

    ee::TrackTransformRuntime runtime;
    runtime.ResetBase(floors);
    if (!runtime.SetTimeline(events.data(), static_cast<std::uint32_t>(events.size())))
    {
        std::cerr << "FAIL: track-transform timeline setup rejected valid input.\n";
        std::exit(1);
    }

    // Every event has fully completed long before this point. There are no
    // currently animating transforms, so steady-state Update cost should not
    // grow with the total number of historical tracks.
    runtime.SetPlaybackAnchor(2000.0, 1.0, EE_PLAYBACK_FLAG_ACTIVE);

    // First update may legitimately settle completed values. Measure only the
    // steady-state frames that follow it.
    runtime.Update(floors, cells);

    constexpr int Iterations = 3;
    const auto started = std::chrono::steady_clock::now();
    for (int i = 0; i < Iterations; ++i)
        runtime.Update(floors, cells);
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

    std::cout << "track-transform Update " << SmallFloorCount
              << " finished tracks: " << small_ms << " ms\n";
    std::cout << "track-transform Update " << LargeFloorCount
              << " finished tracks: " << large_ms << " ms\n";
    std::cout << "scale ratio for 8x historical tracks: " << ratio << "x\n";

    if (ratio > MaxScaleRatio)
    {
        std::cerr
            << "FAIL: RED: TrackTransformRuntime::Update rescans settled historical tracks every frame. "
            << "8x finished tracks took " << ratio << "x time; limit=" << MaxScaleRatio << "x.\n";
        return 1;
    }

    std::cout << "PASS: settled historical transforms do not dominate steady-state Update cost.\n";
    return 0;
}
