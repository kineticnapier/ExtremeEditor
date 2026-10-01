#include "track_visual_runtime.h"

#include <cmath>
#include <iostream>
#include <vector>

namespace
{
constexpr std::uint32_t Red = 0xff0000ffu;
constexpr std::uint32_t Blue = 0xffff0000u;
constexpr std::uint32_t Green = 0xff00ff00u;

EeFloor Base()
{
    EeFloor floor{};
    floor.track_primary_color = Red;
    floor.track_secondary_color = 0xffffffffu;
    floor.track_visual_flags = EE_TRACK_VISUAL_ENABLED;
    floor.track_anim_duration = 2.0f;
    floor.track_pulse_length = 4u;
    return floor;
}

EeTrackVisualEvent Event(double start, double duration, std::uint32_t color, int source)
{
    EeTrackVisualEvent item{};
    item.start_time = start;
    item.transition_duration = duration;
    item.start_floor = 0;
    item.end_floor = 0;
    item.primary_color = color;
    item.secondary_color = color;
    item.visual_flags = EE_TRACK_VISUAL_ENABLED;
    item.anim_duration = 2.0f;
    item.pulse_length = 4u;
    item.source_index = source;
    return item;
}

bool NearByte(std::uint32_t color, int shift, int expected)
{
    return std::abs(static_cast<int>((color >> shift) & 0xffu) - expected) <= 1;
}

bool Expect(bool condition, const char* message)
{
    if (!condition)
        std::cerr << message << '\n';
    return condition;
}
}

int main()
{
    bool ok = true;
    ee::TrackVisualRuntime runtime;
    std::vector<EeFloor> floors(6, Base());
    floors[0].x = 42.0f;
    floors[0].transform_scale_x = 3.0f;
    runtime.ResetBase(floors);

    EeTrackVisualEvent single = Event(10.0, 2.0, Blue, 1);
    ok &= Expect(runtime.SetTimeline(&single, 1u), "single timeline rejected");
    runtime.EvaluateAt(9.0, floors);
    ok &= Expect(floors[0].track_primary_color == Red, "event applied before start");
    runtime.EvaluateAt(11.0, floors);
    ok &= Expect(NearByte(floors[0].track_primary_color, 0, 128) &&
                 NearByte(floors[0].track_primary_color, 16, 128), "single midpoint mismatch");
    runtime.EvaluateAt(12.0, floors);
    ok &= Expect(floors[0].track_primary_color == Blue, "single target mismatch");
    ok &= Expect(floors[0].x == 42.0f && floors[0].transform_scale_x == 3.0f,
                 "track visual runtime overwrote transform fields");

    EeTrackVisualEvent overlap[] = {Event(10.0, 4.0, Blue, 2), Event(12.0, 4.0, Green, 3)};
    ok &= Expect(runtime.SetTimeline(overlap, 2u), "overlap timeline rejected");
    runtime.EvaluateAt(12.0, floors);
    ok &= Expect(floors[0].track_primary_color == Blue, "Kill(complete:true) start mismatch");
    runtime.EvaluateAt(14.0, floors);
    ok &= Expect(NearByte(floors[0].track_primary_color, 8, 128) &&
                 NearByte(floors[0].track_primary_color, 16, 128), "overlap midpoint mismatch");
    runtime.EvaluateAt(5.0, floors);
    ok &= Expect(floors[0].track_primary_color == Red, "backward seek did not restore base");

    EeTrackVisualEvent stripes = Event(0.0, 0.0, Blue, 4);
    stripes.start_floor = 1;
    stripes.end_floor = 5;
    stripes.gap_length = 1;
    stripes.secondary_color = Green;
    stripes.visual_flags = EE_TRACK_VISUAL_ENABLED | 1u;
    ok &= Expect(runtime.SetTimeline(&stripes, 1u), "stripes timeline rejected");
    runtime.EvaluateAt(0.0, floors);
    ok &= Expect(floors[1].track_primary_color == Blue &&
                 floors[3].track_primary_color == Blue &&
                 floors[2].track_primary_color == Red,
                 "range/gap/stripes selection mismatch");

    EeTrackVisualEvent same[] = {Event(0.0, 0.0, Blue, 10), Event(0.0, 0.0, Green, 11)};
    ok &= Expect(runtime.SetTimeline(same, 2u), "same-time timeline rejected");
    runtime.EvaluateAt(0.0, floors);
    ok &= Expect(floors[0].track_primary_color == Green, "same-time source ordering mismatch");

    ok &= Expect(runtime.SetTimeline(&single, 1u), "pause timeline rejected");
    runtime.SetPlaybackAnchor(11.0, 1.0, EE_PLAYBACK_FLAG_ACTIVE);
    runtime.Update(floors);
    ok &= Expect(NearByte(floors[0].track_primary_color, 0, 128) &&
                 NearByte(floors[0].track_primary_color, 16, 128), "paused anchor was not stable");
    return ok ? 0 : 1;
}
