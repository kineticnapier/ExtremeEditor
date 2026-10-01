#include "track_animation_runtime.h"

#include <cmath>
#include <cstdio>
#include <vector>

namespace
{
bool Near(float a, float b, float eps = 0.0005f)
{
    return std::abs(a - b) <= eps;
}

EeFloor Floor(float x)
{
    EeFloor result{};
    result.x = x;
    result.y = 0.0f;
    result.entry_angle = 0.0f;
    result.transform_scale_x = 1.0f;
    result.transform_scale_y = 1.0f;
    result.transform_opacity = 1.0f;
    result.track_transform_flags = EE_TRACK_TRANSFORM_ENABLED;
    return result;
}

EeTrackAnimationSegment Segment(std::uint32_t appear, std::uint32_t disappear)
{
    EeTrackAnimationSegment item{};
    item.start_floor = 0;
    item.end_floor = 1;
    item.appear_type = appear;
    item.disappear_type = disappear;
    item.beats_ahead = 1.0f;
    item.beats_behind = 1.0f;
    item.appear_reference_speed = 1.0f;
    item.disappear_reference_speed = 1.0f;
    item.pitch = 1.0f;
    item.source_index = 7;
    return item;
}

bool Evaluate(
    std::uint32_t appear,
    std::uint32_t disappear,
    double chart_time,
    std::uint32_t floor,
    EeFloor& output,
    ee::TrackAnimationFloorState& state)
{
    std::vector<EeFloor> base{Floor(0.0f), Floor(4.0f)};
    EeTrackAnimationTiming timings[2]{};
    timings[0].entry_time = 2.0;
    timings[0].beat_seconds_no_pitch = 1.0f;
    timings[0].speed = 1.0f;
    timings[1].entry_time = 4.0;
    timings[1].beat_seconds_no_pitch = 1.0f;
    timings[1].speed = 1.0f;
    EeTrackAnimationSegment segment = Segment(appear, disappear);

    ee::TrackAnimationRuntime runtime;
    runtime.ResetBase(base);
    if (!runtime.SetTimeline(&segment, 1u, timings, 2u))
        return false;
    const EeFloor* previous = floor > 0 ? &base[floor - 1] : nullptr;
    const EeFloor* next = floor + 1 < base.size() ? &base[floor + 1] : nullptr;
    return runtime.EvaluateForFloor(
        floor, chart_time, base[floor], previous, next, output, state);
}
}

int main()
{
    int failures = 0;
    auto require = [&](bool condition, const char* name)
    {
        if (!condition)
        {
            std::fprintf(stderr, "FAIL: %s\n", name);
            ++failures;
        }
    };

    {
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(Evaluate(EE_TRACK_APPEAR_GROW, EE_TRACK_DISAPPEAR_NONE, 1.0, 0, value, state), "grow evaluate");
        require(Near(value.transform_scale_x, 0.0f), "grow starts at zero");
        require(Evaluate(EE_TRACK_APPEAR_GROW, EE_TRACK_DISAPPEAR_NONE, 1.25, 0, value, state), "grow midpoint evaluate");
        require(Near(value.transform_scale_x, 0.75f), "grow midpoint OutQuad");
        require(Evaluate(EE_TRACK_APPEAR_GROW, EE_TRACK_DISAPPEAR_NONE, 2.0, 0, value, state), "grow complete evaluate");
        require(Near(value.transform_scale_x, 1.0f), "grow completes");
    }

    {
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(Evaluate(EE_TRACK_APPEAR_FADE, EE_TRACK_DISAPPEAR_NONE, 1.25, 0, value, state), "fade midpoint evaluate");
        require(Near(value.transform_opacity, 0.5f), "fade linear midpoint");
    }

    {
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        // Drop starts at entry - 2*beatsAhead = 0 and lasts one second.
        require(Evaluate(EE_TRACK_APPEAR_DROP, EE_TRACK_DISAPPEAR_NONE, 0.0, 0, value, state), "drop start evaluate");
        require(Near(value.y, 8.0f), "drop starts +8");
        require(Near(value.transform_scale_x, 0.0f), "drop starts scale zero");
        require(Evaluate(EE_TRACK_APPEAR_RISE, EE_TRACK_DISAPPEAR_NONE, 0.0, 0, value, state), "rise start evaluate");
        require(Near(value.y, -8.0f), "rise starts -8");
    }

    {
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        // floor0 disappear begins at next entry 4 + beatsBehind 1 = 5.
        require(Evaluate(EE_TRACK_APPEAR_NONE, EE_TRACK_DISAPPEAR_SHRINK, 5.25, 0, value, state), "shrink midpoint evaluate");
        require(Near(value.transform_scale_x, 0.25f), "shrink OutQuad midpoint");
        require(Evaluate(EE_TRACK_APPEAR_NONE, EE_TRACK_DISAPPEAR_FADE, 5.25, 0, value, state), "disappear fade evaluate");
        require(Near(value.transform_opacity, 0.5f), "disappear fade midpoint");
    }

    {
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(Evaluate(EE_TRACK_APPEAR_NONE, EE_TRACK_DISAPPEAR_RETRACT, 5.25, 0, value, state), "retract evaluate");
        require(value.x > 0.0f && value.x < 4.0f, "retract targets next runtime position");
        require(state.sorting_offset == -3, "retract sorting offset");
        require(Evaluate(EE_TRACK_APPEAR_NONE, EE_TRACK_DISAPPEAR_SCATTER, 5.25, 0, value, state), "scatter evaluate");
        require(state.sorting_offset == -1, "scatter sorting offset");
    }

    {
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(Evaluate(EE_TRACK_APPEAR_EXTEND, EE_TRACK_DISAPPEAR_NONE, 3.0, 1, value, state), "extend evaluate");
        require(value.x < 4.0f, "extend starts from previous floor direction");
        require(state.extend_anim >= 0.0f && state.extend_anim <= 1.0f, "extend state range");
    }

    if (failures != 0)
        return 1;
    std::puts("track_animation_runtime_tests: PASS");
    return 0;
}
