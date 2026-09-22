#include "renderer_camera.h"
#include "level_scene.h"

#include <cmath>
#include <cstdlib>
#include <iostream>
#include <vector>

namespace
{
void ExpectNear(float actual, float expected, float epsilon, const char* message)
{
    if (std::fabs(actual - expected) <= epsilon)
        return;

    std::cerr << "FAIL: " << message
              << " expected=" << expected
              << " actual=" << actual << '\n';
    std::exit(1);
}

EeCameraEvent MakeTileEvent()
{
    EeCameraEvent item{};
    item.start_time = 0.0;
    item.duration_seconds = 10.0;
    item.start_x = 30.0f;   // World camera position when the tween starts.
    item.start_y = 40.0f;
    item.target_x = 10.0f;  // Tile-local MoveCamera offset.
    item.target_y = -5.0f;
    item.flags = EE_CAMERA_APPLY_X | EE_CAMERA_APPLY_Y;
    item.ease = EE_CAMERA_EASE_LINEAR;
    item.reference_floor = 0;
    item.reference_flags = EE_CAMERA_REFERENCE_TILE;
    return item;
}

ee::PlaybackVisualState MakePlayback()
{
    ee::PlaybackVisualState playback{};
    playback.active = true;
    playback.stationary_x = 0.0f;
    playback.stationary_y = 0.0f;
    return playback;
}

void VerifyTileTweenStartRemainsWorldFixed()
{
    ee::LevelScene scene;
    EeFloor floor{};
    floor.x = 200.0f;
    floor.y = 80.0f;
    scene.floors.push_back(floor);

    const std::vector<EeCameraEvent> events{MakeTileEvent()};
    const ee::CameraVisualState result = ee::CalculateCameraVisual(
        &scene,
        &events,
        MakePlayback(),
        0.0);

    // RED: moving the reference tile must not translate the tween's already
    // captured world-space start. Current implementation adds the runtime Tile
    // position to both start and target, producing (230, 120) here.
    ExpectNear(result.x, 30.0f, 0.0001f,
        "Tile camera tween start must stay in captured world space");
    ExpectNear(result.y, 40.0f, 0.0001f,
        "Tile camera tween start Y must stay in captured world space");
}

void VerifyOnlyTargetTracksRuntimeTileDuringTween()
{
    ee::LevelScene scene;
    EeFloor floor{};
    floor.x = 100.0f;
    floor.y = 50.0f;
    scene.floors.push_back(floor);

    const std::vector<EeCameraEvent> events{MakeTileEvent()};
    const ee::PlaybackVisualState playback = MakePlayback();

    const ee::CameraVisualState first = ee::CalculateCameraVisual(
        &scene,
        &events,
        playback,
        5.0);

    // StartWorld=(30,40), TargetWorld=(110,45), t=0.5.
    ExpectNear(first.x, 70.0f, 0.0001f,
        "Tile camera midpoint must interpolate from fixed world start to runtime target");
    ExpectNear(first.y, 42.5f, 0.0001f,
        "Tile camera midpoint Y must interpolate from fixed world start to runtime target");

    scene.floors[0].x = 200.0f;
    scene.floors[0].y = 80.0f;

    const ee::CameraVisualState moved = ee::CalculateCameraVisual(
        &scene,
        &events,
        playback,
        5.0);

    // Same captured start, but runtime target becomes (210,75).
    ExpectNear(moved.x, 120.0f, 0.0001f,
        "Only the Tile-relative target may move when MoveTrack changes the reference floor");
    ExpectNear(moved.y, 57.5f, 0.0001f,
        "Only the Tile-relative target Y may move when MoveTrack changes the reference floor");

    const ee::CameraVisualState completed = ee::CalculateCameraVisual(
        &scene,
        &events,
        playback,
        10.0);
    ExpectNear(completed.x, 210.0f, 0.0001f,
        "Completed Tile camera target must equal runtime Tile X plus MoveCamera offset");
    ExpectNear(completed.y, 75.0f, 0.0001f,
        "Completed Tile camera target must equal runtime Tile Y plus MoveCamera offset");
}
}

int main()
{
    VerifyTileTweenStartRemainsWorldFixed();
    VerifyOnlyTargetTracksRuntimeTileDuringTween();
    std::cout << "PASS: camera runtime Tile reference regressions are valid.\n";
    return 0;
}
