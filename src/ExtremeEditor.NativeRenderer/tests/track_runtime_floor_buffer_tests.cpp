#include "level_scene.h"

#include <cstdio>
#include <cstring>
#include <memory>
#include <vector>

// The track runtimes read an immutable copy of the uploaded floors. On a
// 16M-floor chart each EeFloor array is about 1.3 GiB, so the runtimes must
// share one base and allocate their own floor-sized buffers only while a
// timeline actually uses them.
namespace
{
int failures = 0;

void Check(bool condition, const char* message)
{
    if (!condition)
    {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++failures;
    }
}

std::vector<EeFloor> Floors(std::uint32_t count)
{
    std::vector<EeFloor> floors(count);
    for (std::uint32_t i = 0; i < count; ++i)
    {
        EeFloor& floor = floors[i];
        floor.x = static_cast<float>(i);
        floor.y = static_cast<float>(i % 7u);
        floor.entry_angle = 0.25f * static_cast<float>(i % 4u);
        floor.icon_id = EE_ICON_NONE;
        floor.track_primary_color = 0xff000000u | i;
        floor.transform_scale_x = 1.0f;
        floor.transform_scale_y = 1.0f;
        floor.transform_opacity = 1.0f;
        floor.track_extend_anim = 1.0f;
    }
    return floors;
}

std::shared_ptr<ee::LevelScene> CreateScene(const std::vector<EeFloor>& floors)
{
    const EeGeometry geometry{0u, 3u};
    const EePoint points[] = {{-0.5f, -0.5f}, {0.5f, -0.5f}, {0.0f, 0.5f}};
    return ee::LevelScene::Create(
        floors.data(),
        static_cast<std::uint32_t>(floors.size()),
        &geometry,
        1u,
        points,
        3u,
        0.0f,
        0.0f,
        static_cast<float>(floors.size()),
        8.0f);
}
}

int main()
{
    constexpr std::uint32_t floor_count = 4096;
    const std::vector<EeFloor> source = Floors(floor_count);
    std::shared_ptr<ee::LevelScene> scene = CreateScene(source);
    Check(scene != nullptr, "scene creation failed");
    if (!scene)
        return 1;

    ee::LevelScene::RuntimeFloorBufferReport report = scene->RuntimeFloorBuffers();
    Check(report.transform_base != nullptr, "transform runtime has no base floors");
    Check(report.transform_base == report.visual_base && report.visual_base == report.animation_base,
        "track runtimes keep separate copies of the base floors");
    Check(report.transform_base != scene->floors.data(),
        "runtime base aliases the live floors the runtimes write to");
    Check(report.owned_floor_buffer_bytes == 0u,
        "track runtimes allocate floor-sized buffers without any timeline");
    if (report.transform_base != nullptr)
    {
        Check(std::memcmp(report.transform_base, source.data(), sizeof(EeFloor) * floor_count) == 0,
            "runtime base differs from the uploaded floors");
        scene->floors[5].x = 1000.0f;
        Check(report.transform_base[5].x == source[5].x, "writing live floors changed the runtime base");
    }

    // An animation timeline needs its per-floor working state; removing it
    // releases that state again.
    std::vector<EeTrackAnimationTiming> timings(floor_count);
    for (std::uint32_t i = 0; i < floor_count; ++i)
    {
        timings[i].entry_time = static_cast<double>(i);
        timings[i].beat_seconds_no_pitch = 1.0f;
        timings[i].speed = 1.0f;
    }
    EeTrackAnimationSegment segment{};
    segment.start_floor = 10;
    segment.end_floor = 20;
    segment.appear_type = EE_TRACK_APPEAR_EXTEND;
    segment.beats_ahead = 1.0f;
    segment.beats_behind = 1.0f;
    segment.appear_reference_speed = 1.0f;
    segment.disappear_reference_speed = 1.0f;
    segment.pitch = 1.0f;
    Check(scene->SetTrackAnimationTimeline(&segment, 1u, timings.data(), floor_count),
        "animation timeline was rejected");
    Check(scene->RuntimeFloorBuffers().owned_floor_buffer_bytes > 0u,
        "active animation timeline has no per-floor state");

    Check(scene->SetTrackAnimationTimeline(nullptr, 0u, timings.data(), floor_count),
        "timings without segments were rejected");
    Check(scene->RuntimeFloorBuffers().owned_floor_buffer_bytes == 0u,
        "timings without segments keep floor-sized animation buffers");
    const ee::TrackAnimationFloorState state = scene->TrackAnimationState(15);
    Check(state.extend_anim == 1.0f && state.sorting_offset == 0 && !state.active_position &&
              !state.active_scale && !state.active_rotation && !state.active_opacity,
        "inactive animation reports a non-default floor state");

    Check(scene->SetTrackTransformTimeline(nullptr, 0u), "empty transform timeline was rejected");
    Check(scene->RuntimeFloorBuffers().owned_floor_buffer_bytes == 0u,
        "empty transform timeline keeps a floor-sized lookup");

    if (failures == 0)
        std::puts("PASS: track runtimes share one base and allocate floor buffers lazily");
    return failures == 0 ? 0 : 1;
}
