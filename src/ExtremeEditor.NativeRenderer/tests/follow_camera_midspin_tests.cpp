#include "follow_camera_state.h"

#include <cmath>
#include <cstdlib>
#include <iostream>
#include <vector>

namespace
{
void ExpectNear(float actual, float expected, float epsilon, const char* phase)
{
    if (std::fabs(actual - expected) <= epsilon)
        return;

    std::cerr
        << "FAIL: Midspin equal-time floors were processed as independent native follow-camera transitions. "
        << "phase=" << phase
        << " expected=" << expected
        << " actual=" << actual << '\n';
    std::exit(1);
}

ee::PlaybackVisualState Playback(std::int32_t floor, float x)
{
    ee::PlaybackVisualState result{};
    result.active = true;
    result.floor = floor;
    result.stationary_x = x;
    return result;
}


void VerifyHighBpmFrameSkipMatchesIncrementalFollow()
{
    constexpr int Count = 128;
    constexpr double Interval = 0.01; // 100 floor transitions per second
    ee::LevelScene scene;
    scene.floors.resize(Count);
    std::vector<EePlaybackTiming> timings(Count);
    for (int i = 0; i < Count; ++i)
    {
        scene.floors[i].x = static_cast<float>(i) * 1.5f;
        timings[i].entry_time = i * Interval;
        timings[i].exit_time = (i + 1) * Interval;
        timings[i].angle_moved = 3.1415927f;
    }

    ee::FollowCameraState stepped;
    ee::FollowCameraState skipped;
    constexpr std::uint64_t version = 1;
    stepped.Update(&scene, &timings, Playback(0, 0.0f), 0.0, version);
    skipped.Update(&scene, &timings, Playback(0, 0.0f), 0.0, version);
    for (int frame = 1; frame <= 80; ++frame)
    {
        const double time = frame * Interval;
        const int floor = frame;
        stepped.Update(&scene, &timings,
            Playback(floor, scene.floors[floor].x), time, version);
    }
    skipped.Update(&scene, &timings,
        Playback(80, scene.floors[80].x), 0.8, version);
    ExpectNear(skipped.x, stepped.x, 0.0001f, "high-bpm-frame-skip");
    // Camera smoothing is allowed to lag the player but must stay finite.
    if (!std::isfinite(skipped.x) ||
        skipped.x > scene.floors[80].x + 0.001f)
    {
        std::cerr << "FAIL: follow camera overshot high-BPM planet position.\n";
        std::exit(1);
    }
}

void VerifyMidspinEqualTimeRunStartsOneFollowTransition()
{
    ee::LevelScene scene;
    scene.floors.resize(4);
    scene.floors[0].x = 0.0f;
    scene.floors[1].x = 10.0f; // Midspin transition target.
    scene.floors[2].x = 20.0f; // Equal-time following floor.
    scene.floors[3].x = 30.0f;

    std::vector<EePlaybackTiming> timings(4);
    timings[0].entry_time = 0.0;
    timings[0].exit_time = 1.0;
    timings[0].angle_moved = 3.1415927f;

    timings[1].entry_time = 1.0;
    timings[1].exit_time = 1.0;
    timings[1].angle_moved = 0.0f;

    timings[2].entry_time = 1.0;
    timings[2].exit_time = 2.0;
    timings[2].angle_moved = 3.1415927f;

    timings[3].entry_time = 2.0;
    timings[3].exit_time = 3.0;
    timings[3].angle_moved = 3.1415927f;

    ee::FollowCameraState state;
    constexpr std::uint64_t scene_version = 1;
    state.Update(&scene, &timings, Playback(0, 0.0f), 0.0, scene_version);

    state.Update(&scene, &timings, Playback(0, 0.0f), 0.999, scene_version);
    ExpectNear(state.x, 0.0f, 0.0001f, "before");

    state.Update(&scene, &timings, Playback(2, 20.0f), 1.0, scene_version);
    ExpectNear(state.x, 0.0f, 0.0001f, "at-entry");

    state.Update(&scene, &timings, Playback(2, 20.0f), 1.001, scene_version);
    ExpectNear(state.x, 0.005f, 0.0001f, "immediately-after");

    state.Update(&scene, &timings, Playback(2, 20.0f), 2.0, scene_version);
    ExpectNear(state.x, 5.0f, 0.0001f, "follow-midpoint");
}
}

int main()
{
    VerifyMidspinEqualTimeRunStartsOneFollowTransition();
    VerifyHighBpmFrameSkipMatchesIncrementalFollow();
    std::cout << "PASS: native Midspin follow-camera regression is valid.\n";
    return 0;
}
