#include "track_transform_runtime.h"

#include <chrono>
#include <cstdlib>
#include <iostream>
#include <vector>

int main()
{
    constexpr std::uint32_t FloorCount = 12000u;
    constexpr double MaxMilliseconds = 1000.0;

    std::vector<EeFloor> floors(FloorCount);
    for (std::uint32_t i = 0; i < FloorCount; ++i)
    {
        floors[i].x = static_cast<float>(i);
        floors[i].y = 0.0f;
    }

    std::vector<EeTrackTransformEvent> events(FloorCount);
    for (std::uint32_t i = 0; i < FloorCount; ++i)
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
    const double elapsed_ms = std::chrono::duration<double, std::milli>(finished - started).count();

    if (!ok)
    {
        std::cerr << "FAIL: track-transform timeline setup rejected valid input.\n";
        return 1;
    }

    std::cout << "track-transform SetTimeline " << FloorCount
              << " unique floors: " << elapsed_ms << " ms\n";

    if (elapsed_ms > MaxMilliseconds)
    {
        std::cerr
            << "FAIL: RED: TrackTransformRuntime::SetTimeline must not linearly scan all existing floor tracks for every event. "
            << "elapsed=" << elapsed_ms << "ms limit=" << MaxMilliseconds << "ms\n";
        return 1;
    }

    std::cout << "PASS: track-transform timeline grouping scales for large levels.\n";
    return 0;
}
