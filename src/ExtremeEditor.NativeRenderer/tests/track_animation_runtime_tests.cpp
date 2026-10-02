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
    result.track_extend_anim = 1.0f;
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
        require(value.track_sorting_offset == -3, "retract renderer sorting state");
        require(Evaluate(EE_TRACK_APPEAR_NONE, EE_TRACK_DISAPPEAR_SCATTER, 5.25, 0, value, state), "scatter evaluate");
        require(state.sorting_offset == -1, "scatter sorting offset");
        require(value.track_sorting_offset == -1, "scatter renderer sorting state");
    }

    {
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(Evaluate(EE_TRACK_APPEAR_EXTEND, EE_TRACK_DISAPPEAR_NONE, 3.0, 1, value, state), "extend evaluate");
        require(value.x < 4.0f, "extend starts from previous floor direction");
        require(state.extend_anim >= 0.0f && state.extend_anim <= 1.0f, "extend state range");
        require(Near(value.track_extend_anim, state.extend_anim), "extend renderer mesh state");
    }

    {
        // Persistent compact segments select by floor without per-floor event expansion.
        std::vector<EeFloor> base{Floor(0.0f), Floor(1.0f), Floor(2.0f), Floor(3.0f)};
        EeTrackAnimationTiming timings[4]{};
        for (int i = 0; i < 4; ++i)
        {
            timings[i].entry_time = static_cast<double>(i + 2);
            timings[i].beat_seconds_no_pitch = 1.0f;
            timings[i].speed = 1.0f;
        }
        EeTrackAnimationSegment segments[2]{};
        segments[0] = Segment(EE_TRACK_APPEAR_GROW, EE_TRACK_DISAPPEAR_NONE);
        segments[0].start_floor = 0;
        segments[0].end_floor = 1;
        segments[1] = Segment(EE_TRACK_APPEAR_FADE, EE_TRACK_DISAPPEAR_NONE);
        segments[1].start_floor = 2;
        segments[1].end_floor = 3;
        ee::TrackAnimationRuntime runtime;
        runtime.ResetBase(base);
        require(runtime.SetTimeline(segments, 2u, timings, 4u), "persistent segment setup");
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(runtime.EvaluateForFloor(1u, 2.0, base[1], &base[0], &base[2], value, state),
            "persistent first segment lookup");
        require(state.active_scale, "persistent Grow state");
        require(runtime.EvaluateForFloor(2u, 3.0, base[2], &base[1], &base[3], value, state),
            "persistent second segment lookup");
        require(state.active_opacity, "persistent Fade state");
    }

    {
        // SetSpeed correction changes configured beats through floor/reference speed;
        // pitch only shortens duration and never moves the trigger time.
        std::vector<EeFloor> base{Floor(0.0f), Floor(1.0f)};
        EeTrackAnimationTiming timings[2]{};
        timings[0].entry_time = 10.0;
        timings[0].beat_seconds_no_pitch = 0.25f;
        timings[0].speed = 2.0f;
        timings[1].entry_time = 11.0;
        timings[1].beat_seconds_no_pitch = 0.25f;
        timings[1].speed = 2.0f;
        EeTrackAnimationSegment segment = Segment(EE_TRACK_APPEAR_GROW, EE_TRACK_DISAPPEAR_NONE);
        segment.start_floor = 0;
        segment.end_floor = 0;
        segment.beats_ahead = 3.0f;
        segment.appear_reference_speed = 1.0f;
        segment.pitch = 2.0f;
        ee::TrackAnimationRuntime runtime;
        runtime.ResetBase(base);
        require(runtime.SetTimeline(&segment, 1u, timings, 2u), "speed correction setup");
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        // effective beats=6, trigger=8.5, duration=.0625. At trigger scale is zero.
        require(runtime.EvaluateForFloor(0u, 8.5, base[0], nullptr, &base[1], value, state),
            "speed correction evaluate");
        require(Near(value.transform_scale_x, 0.0f), "speed-corrected trigger");
        require(runtime.EvaluateForFloor(0u, 8.53125, base[0], nullptr, &base[1], value, state),
            "pitch duration evaluate");
        require(Near(value.transform_scale_x, 0.75f), "pitch-shortened OutQuad midpoint");
    }

    {
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(Evaluate(EE_TRACK_APPEAR_GROW_SPIN, EE_TRACK_DISAPPEAR_NONE, 1.0, 0, value, state),
            "grow spin start evaluate");
        require(Near(value.transform_scale_x, 0.0f), "grow spin initial scale");
        require(Near(value.transform_rotation, -3.14159265f), "grow spin initial rotation");
        require(Evaluate(EE_TRACK_APPEAR_DROP, EE_TRACK_DISAPPEAR_NONE, 0.125, 0, value, state),
            "drop scale eighth evaluate");
        require(Near(value.transform_scale_x, 1.0f), "drop scale completes at duration eighth");
    }

    {
        // Explicit chart-time evaluation is deterministic for pause and backward seeks.
        EeFloor before{}, midpoint{}, complete{}, rewind{}, paused{};
        ee::TrackAnimationFloorState state{};
        require(Evaluate(EE_TRACK_APPEAR_FADE, EE_TRACK_DISAPPEAR_NONE, 0.5, 0, before, state),
            "fade before trigger");
        require(Evaluate(EE_TRACK_APPEAR_FADE, EE_TRACK_DISAPPEAR_NONE, 1.25, 0, midpoint, state),
            "fade midpoint");
        require(Evaluate(EE_TRACK_APPEAR_FADE, EE_TRACK_DISAPPEAR_NONE, 2.0, 0, complete, state),
            "fade complete");
        require(Evaluate(EE_TRACK_APPEAR_FADE, EE_TRACK_DISAPPEAR_NONE, 1.125, 0, rewind, state),
            "fade backward seek");
        require(Evaluate(EE_TRACK_APPEAR_FADE, EE_TRACK_DISAPPEAR_NONE, 1.25, 0, paused, state),
            "fade paused repeat");
        require(Near(before.transform_opacity, 0.0f), "before trigger initial state");
        require(Near(midpoint.transform_opacity, 0.5f), "seek midpoint");
        require(Near(complete.transform_opacity, 1.0f), "seek complete");
        require(Near(rewind.transform_opacity, 0.25f), "backward seek reconstruction");
        require(Near(paused.transform_opacity, midpoint.transform_opacity), "paused chart time stable");
    }

    {
        // Animate combined position/scale preserves MoveTrack axis deltas instead of
        // deleting the separately-keyed MoveTrack channel.
        std::vector<EeFloor> base{Floor(10.0f), Floor(20.0f)};
        EeTrackAnimationTiming timings[2]{};
        timings[0].entry_time = 2.0;
        timings[0].beat_seconds_no_pitch = 1.0f;
        timings[0].speed = 1.0f;
        timings[1] = timings[0];
        EeTrackAnimationSegment segment = Segment(EE_TRACK_APPEAR_EXTEND, EE_TRACK_DISAPPEAR_NONE);
        segment.start_floor = 1;
        segment.end_floor = 1;
        ee::TrackAnimationRuntime runtime;
        runtime.ResetBase(base);
        require(runtime.SetTimeline(&segment, 1u, timings, 2u), "parallel channel setup");
        EeFloor move = base[1];
        move.x += 3.0f;
        move.transform_scale_x = 1.5f;
        move.transform_scale_y = 1.25f;
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(runtime.EvaluateForFloor(1u, 1.25, move, &base[0], nullptr, value, state),
            "parallel channel evaluate");
        require(value.x > 10.0f && value.x < 23.0f, "Extend and Move position both represented");
        require(value.transform_scale_x > value.transform_scale_y,
            "Move scale-axis delta survives Animate combined scale");

        std::vector<EeFloor> positioned = base;
        positioned[1].transform_scale_x = 1.75f;
        positioned[1].transform_scale_y = 1.75f;
        runtime.ResetBase(positioned);
        require(runtime.SetTimeline(&segment, 1u, timings, 2u), "PositionTrack base setup");
        require(runtime.EvaluateForFloor(1u, 4.0, positioned[1], &positioned[0], nullptr, value, state),
            "PositionTrack target evaluate");
        require(Near(value.transform_scale_x, 1.0f) && Near(value.transform_scale_y, 1.0f),
            "Animate completed scale target is one, not PositionTrack startScale");
    }

    {
        // Terminal Retract has no next-floor capture and therefore no effect.
        std::vector<EeFloor> base{Floor(0.0f)};
        EeTrackAnimationTiming timing{};
        timing.entry_time = 2.0;
        timing.beat_seconds_no_pitch = 1.0f;
        timing.speed = 1.0f;
        EeTrackAnimationSegment segment = Segment(EE_TRACK_APPEAR_NONE, EE_TRACK_DISAPPEAR_RETRACT);
        segment.start_floor = 0;
        segment.end_floor = 0;
        ee::TrackAnimationRuntime runtime;
        runtime.ResetBase(base);
        require(runtime.SetTimeline(&segment, 1u, &timing, 1u), "terminal Retract setup");
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(runtime.EvaluateForFloor(0u, 100.0, base[0], nullptr, nullptr, value, state),
            "terminal Retract evaluate");
        require(state.sorting_offset == 0 && Near(value.transform_scale_x, 1.0f),
            "terminal Retract remains unchanged");
    }

    {
        // Re-evaluating the same paused chart time must not feed the prior Animate
        // result back into the MoveTrack/base input and drift toward another value.
        std::vector<EeFloor> floors{Floor(0.0f), Floor(4.0f)};
        EeTrackAnimationTiming timings[2]{};
        timings[0].entry_time = 2.0;
        timings[0].beat_seconds_no_pitch = 1.0f;
        timings[0].speed = 1.0f;
        timings[1].entry_time = 4.0;
        timings[1].beat_seconds_no_pitch = 1.0f;
        timings[1].speed = 1.0f;
        EeTrackAnimationSegment segment = Segment(EE_TRACK_APPEAR_GROW, EE_TRACK_DISAPPEAR_NONE);
        ee::TrackAnimationRuntime runtime;
        runtime.ResetBase(floors);
        require(runtime.SetTimeline(&segment, 1u, timings, 2u), "stable paused setup");
        runtime.SetPlaybackAnchor(1.25, 1.0, EE_PLAYBACK_FLAG_ACTIVE);
        ee::TrackTransformRuntime::CellMap cells;
        cells[0].push_back(0u);
        cells[0].push_back(1u);
        runtime.Update(floors, cells);
        const float first = floors[0].transform_scale_x;
        runtime.Update(floors, cells);
        require(Near(floors[0].transform_scale_x, first), "same-time evaluation does not accumulate");
    }

    {
        // Rotation/Opacity share DOTween keys with MoveTrack. A later MoveTrack
        // owns those channels after completing the older Animate tween target.
        std::vector<EeFloor> base{Floor(0.0f), Floor(4.0f)};
        EeTrackAnimationTiming timings[2]{};
        timings[0].entry_time = 2.0;
        timings[0].beat_seconds_no_pitch = 1.0f;
        timings[0].speed = 1.0f;
        timings[1].entry_time = 4.0;
        timings[1].beat_seconds_no_pitch = 1.0f;
        timings[1].speed = 1.0f;
        EeTrackAnimationSegment segment = Segment(EE_TRACK_APPEAR_GROW_SPIN, EE_TRACK_DISAPPEAR_FADE);
        segment.source_index = 10;
        EeTrackTransformEvent move{};
        move.floor = 0;
        move.start_time = 1.25;
        move.duration_seconds = 0.25;
        move.flags = EE_TRACK_TRANSFORM_ROTATION | EE_TRACK_TRANSFORM_OPACITY;
        move.reserved = 20u;
        ee::TrackAnimationRuntime runtime;
        runtime.ResetBase(base);
        runtime.SetCompetingTimeline(&move, 1u);
        require(runtime.SetTimeline(&segment, 1u, timings, 2u), "shared-key competition setup");
        EeFloor move_value = base[0];
        move_value.transform_rotation = 0.75f;
        move_value.transform_opacity = 0.4f;
        EeFloor value{};
        ee::TrackAnimationFloorState state{};
        require(runtime.EvaluateForFloor(0u, 1.5, move_value, nullptr, &base[1], value, state),
            "shared-key competition evaluate");
        require(Near(value.transform_rotation, 0.75f), "later MoveTrack owns Rotation");

        segment.appear_type = EE_TRACK_APPEAR_FADE;
        segment.disappear_type = EE_TRACK_DISAPPEAR_NONE;
        runtime.ResetBase(base);
        runtime.SetCompetingTimeline(&move, 1u);
        require(runtime.SetTimeline(&segment, 1u, timings, 2u), "opacity competition setup");
        require(runtime.EvaluateForFloor(0u, 1.5, move_value, nullptr, &base[1], value, state),
            "opacity competition evaluate");
        require(Near(value.transform_opacity, 0.4f), "later MoveTrack owns Opacity");
    }

    if (failures != 0)
        return 1;
    std::puts("track_animation_runtime_tests: PASS");
    return 0;
}
