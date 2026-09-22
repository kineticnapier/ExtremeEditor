#include "track_transform_runtime.h"

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
        item.start_time = static_cast<double>(i) * 0.001;
        item.duration_seconds = 1.0;
        item.flags = EE_TRACK_TRANSFORM_X;
        item.start_x = static_cast<float>(i);
        item.target_x = static_cast<float>(i + 1u);
        events[i] = item;
    }

    ee::TrackTransformRuntime runtime;
    runtime.ResetBase(floors);

    const auto started = std::chrono::steady_clock::now();
    const bool ok = runtime.SetTimeline(events.data(), static_cast<std::uint32_t>(events.size()));
    const auto finished = std::chrono::steady_clock::now();

    if (!ok)
    {
        std::cerr << "FAIL: track-transform timeline setup rejected valid input.\n";
        std::exit(1);
    }

    return std::chrono::duration<double, std::milli>(finished - started).count();
}
}

int main()
{
    constexpr std::uint32_t SmallFloorCount = 6000u;
    constexpr std::uint32_t LargeFloorCount = 24000u;
    constexpr double MaxScaleRatio = 8.0;

    // Warm up code/data paths before measuring the scaling ratio.
    (void)Measure(1000u);

    const double small_ms = Measure(SmallFloorCount);
    const double large_ms = Measure(LargeFloorCount);
    const double ratio = large_ms / std::max(0.001, small_ms);

    std::cout << "track-transform SetTimeline " << SmallFloorCount
              << " floors: " << small_ms << " ms\n";
    std::cout << "track-transform SetTimeline " << LargeFloorCount
              << " floors: " << large_ms << " ms\n";
    std::cout << "scale ratio for 4x input: " << ratio << "x\n";

    if (ratio > MaxScaleRatio)
    {
        std::cerr
            << "FAIL: RED: TrackTransformRuntime::SetTimeline scales quadratically when unique floor count grows. "
            << "4x input took " << ratio << "x time; limit=" << MaxScaleRatio << "x.\n";
        return 1;
    }

    std::cout << "PASS: track-transform timeline grouping does not show quadratic scaling.\n";
    return 0;
}
