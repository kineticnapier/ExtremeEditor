#include "floor_transform.h"
#include "track_transform_runtime.h"

#include <cmath>
#include <iostream>
#include <vector>

namespace
{
constexpr float Pi = 3.14159265358979323846f;
constexpr float Epsilon = 0.0001f;

bool Near(float actual, float expected)
{
    return std::abs(actual - expected) <= Epsilon;
}
}

int main()
{
    const float original_entry = Pi * 0.5f;
    const float static_rotation = Pi / 12.0f;
    const float target_rotation = Pi * 0.25f;

    EeFloor base{};
    base.entry_angle = original_entry + static_rotation;
    base.icon_angle = original_entry + static_rotation;
    base.transform_rotation = static_rotation;
    base.transform_scale_x = 3.0f;
    base.transform_scale_y = 1.0f;
    base.transform_opacity = 1.0f;
    base.track_transform_flags = EE_TRACK_TRANSFORM_ENABLED;

    EeTrackTransformEvent event{};
    event.floor = 0;
    event.start_time = 0.0;
    event.duration_seconds = 10.0;
    event.flags = EE_TRACK_TRANSFORM_ROTATION |
        EE_TRACK_TRANSFORM_SCALE_X | EE_TRACK_TRANSFORM_SCALE_Y;
    event.start_rotation = static_rotation;
    event.target_rotation = target_rotation;
    event.start_scale_x = 3.0f;
    event.target_scale_x = 1.0f;
    event.start_scale_y = 1.0f;
    event.target_scale_y = 2.0f;

    ee::TrackTransformRuntime runtime;
    runtime.ResetBase(std::vector<EeFloor>{base});
    if (!runtime.SetTimeline(&event, 1u))
    {
        std::cerr << "MoveTrack transform timeline was rejected\n";
        return 1;
    }

    const auto evaluate = [&](double time)
    {
        runtime.SetPlaybackAnchor(time, 1.0, EE_PLAYBACK_FLAG_ACTIVE);
        EeFloor value = base;
        if (!runtime.EvaluateVisualForFloor(0u, value))
        {
            std::cerr << "MoveTrack visual transform was not evaluated\n";
            std::exit(2);
        }
        return value;
    };

    const EeFloor start = evaluate(0.0);
    const EeFloor middle = evaluate(5.0);
    const EeFloor end = evaluate(10.0);
    const EeFloor seek_back = evaluate(5.0);

    const float middle_rotation = (static_rotation + target_rotation) * 0.5f;
    const auto correct = [&](const EeFloor& value, float rotation, float sx, float sy)
    {
        return Near(value.entry_angle, original_entry + rotation) &&
            Near(value.transform_rotation, rotation) &&
            Near(value.icon_angle, original_entry + rotation) &&
            Near(value.transform_scale_x, sx) &&
            Near(value.transform_scale_y, sy);
    };

    if (!correct(start, static_rotation, 3.0f, 1.0f) ||
        !correct(middle, middle_rotation, 2.0f, 1.5f) ||
        !correct(end, target_rotation, 1.0f, 2.0f))
    {
        std::cerr << "PositionTrack and MoveTrack rotations were added twice\n";
        return 1;
    }

    if (!Near(seek_back.entry_angle, middle.entry_angle) ||
        !Near(seek_back.transform_rotation, middle.transform_rotation) ||
        !Near(seek_back.transform_scale_x, middle.transform_scale_x) ||
        !Near(seek_back.transform_scale_y, middle.transform_scale_y))
    {
        std::cerr << "MoveTrack seek did not reproduce the same transform\n";
        return 1;
    }

    const ee::FloorLocalTransform shape = ee::ResolveFloorLocalTransform(
        middle.entry_angle,
        middle.transform_rotation,
        middle.transform_scale_x,
        middle.transform_scale_y,
        true);
    if (!Near(shape.base_cosine, std::cos(original_entry)) ||
        !Near(shape.base_sine, std::sin(original_entry)))
    {
        std::cerr << "MoveTrack changed the original mesh entry basis\n";
        return 1;
    }

    // MoveTrack alone (no PositionTrack rotation) and two overlapping
    // MoveTrack rotations. Event rotations are absolute transform rotations
    // (C# BuildMoveTimeline), so the later event replaces the earlier one and
    // the original entry basis never changes.
    {
        EeFloor plain{};
        plain.entry_angle = original_entry;
        plain.icon_angle = original_entry;
        plain.transform_rotation = 0.0f;
        plain.transform_scale_x = 1.0f;
        plain.transform_scale_y = 1.0f;
        plain.transform_opacity = 1.0f;
        plain.track_transform_flags = EE_TRACK_TRANSFORM_ENABLED;

        EeTrackTransformEvent moves[2]{};
        moves[0].floor = 0;
        moves[0].start_time = 0.0;
        moves[0].duration_seconds = 10.0;
        moves[0].flags = EE_TRACK_TRANSFORM_ROTATION;
        moves[0].start_rotation = 0.0f;
        moves[0].target_rotation = Pi * 0.5f;
        // DOTween kills the first tween with complete, so the second starts
        // from the first target.
        moves[1].floor = 0;
        moves[1].start_time = 5.0;
        moves[1].duration_seconds = 10.0;
        moves[1].flags = EE_TRACK_TRANSFORM_ROTATION;
        moves[1].start_rotation = Pi * 0.5f;
        moves[1].target_rotation = Pi;

        ee::TrackTransformRuntime overlap;
        overlap.ResetBase(std::vector<EeFloor>{plain});
        if (!overlap.SetTimeline(moves, 2u))
        {
            std::cerr << "overlapping MoveTrack timeline was rejected\n";
            return 1;
        }
        const auto at = [&](double time)
        {
            overlap.SetPlaybackAnchor(time, 1.0, EE_PLAYBACK_FLAG_ACTIVE);
            EeFloor value = plain;
            if (!overlap.EvaluateVisualForFloor(0u, value))
            {
                std::cerr << "overlapping MoveTrack was not evaluated\n";
                std::exit(2);
            }
            return value;
        };
        const struct { double time; float rotation; } samples[] =
        {
            {2.5, Pi * 0.125f},
            {7.5, Pi * 0.625f},
            {20.0, Pi},
            {2.5, Pi * 0.125f}
        };
        for (const auto& sample : samples)
        {
            const EeFloor value = at(sample.time);
            const ee::FloorLocalTransform shape = ee::ResolveFloorLocalTransform(
                value.entry_angle, value.transform_rotation, 1.0f, 1.0f, true);
            if (!Near(value.transform_rotation, sample.rotation) ||
                !Near(value.entry_angle, original_entry + sample.rotation) ||
                !Near(shape.base_cosine, std::cos(original_entry)) ||
                !Near(shape.base_sine, std::sin(original_entry)))
            {
                std::cerr << "MoveTrack-only or overlapping rotation mismatch at "
                          << sample.time << ": rotation " << value.transform_rotation
                          << ", entry " << value.entry_angle << '\n';
                return 1;
            }
        }
    }

    std::cout << "floor MoveTrack transform order and seek passed\n";
    return 0;
}
