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

ee::PlaybackVisualState MakePlayback(float player_x = 0.0f, float player_y = 0.0f)
{
    ee::PlaybackVisualState playback{};
    playback.active = true;
    playback.stationary_x = player_x;
    playback.stationary_y = player_y;
    return playback;
}

EeCameraEvent MakeFrozenTileEvent()
{
    EeCameraEvent item{};
    item.start_time = 0.0;
    item.duration_seconds = 10.0;

    // ffxCameraPlus captures camParent.position when StartEffect runs.
    item.start_x = 30.0f;
    item.start_y = 40.0f;

    // relativeTo=Tile resolves floor.transform.position once at StartEffect.
    // Tile=(100,50), MoveCamera.position=(10,-5) => frozen world target=(110,45).
    item.target_x = 110.0f;
    item.target_y = 45.0f;

    item.flags = EE_CAMERA_APPLY_X | EE_CAMERA_APPLY_Y;
    item.ease = EE_CAMERA_EASE_LINEAR;

    // Keep the metadata populated so this regression catches any evaluator that
    // incorrectly re-resolves the Tile reference every render frame.
    item.reference_floor = 0;
    item.reference_flags = EE_CAMERA_REFERENCE_TILE;
    return item;
}

void VerifyTileTargetIsFrozenWhenEventStarts()
{
    ee::LevelScene scene;
    EeFloor floor{};
    floor.x = 100.0f;
    floor.y = 50.0f;
    scene.floors.push_back(floor);

    const std::vector<EeCameraEvent> events{MakeFrozenTileEvent()};
    const ee::PlaybackVisualState playback = MakePlayback();

    const ee::CameraVisualState before_move = ee::CalculateCameraVisual(
        &scene, &events, playback, 5.0);
    ExpectNear(before_move.x, 70.0f, 0.0001f,
        "ADOFAI Tile camera midpoint must use the world target captured at StartEffect");
    ExpectNear(before_move.y, 42.5f, 0.0001f,
        "ADOFAI Tile camera midpoint Y must use the captured world target");

    // MoveTrack may move the floor after StartEffect, but ffxCameraPlus does not
    // re-read floor.transform.position for the already-running MoveCamera tween.
    scene.floors[0].x = 200.0f;
    scene.floors[0].y = 80.0f;

    const ee::CameraVisualState after_move = ee::CalculateCameraVisual(
        &scene, &events, playback, 5.0);
    ExpectNear(after_move.x, 70.0f, 0.0001f,
        "Running Tile MoveCamera target must not follow later MoveTrack movement");
    ExpectNear(after_move.y, 42.5f, 0.0001f,
        "Running Tile MoveCamera target Y must not follow later MoveTrack movement");

    const ee::CameraVisualState completed = ee::CalculateCameraVisual(
        &scene, &events, playback, 10.0);
    ExpectNear(completed.x, 110.0f, 0.0001f,
        "Completed Tile MoveCamera must end at the frozen StartEffect target X");
    ExpectNear(completed.y, 45.0f, 0.0001f,
        "Completed Tile MoveCamera must end at the frozen StartEffect target Y");
}

void VerifyPlayerToTileSwitchPreservesWorldStart()
{
    ee::LevelScene scene;
    EeFloor floor{};
    floor.x = 500.0f;
    floor.y = 600.0f;
    scene.floors.push_back(floor);

    EeCameraEvent item = MakeFrozenTileEvent();
    item.start_x = 321.0f;
    item.start_y = 123.0f;
    const std::vector<EeCameraEvent> events{item};

    // ffxCameraPlus copies cam.transform.position to camParent.position before
    // SetToFreeMode(). Neither the current player pivot nor the Tile transform may
    // translate that captured world-space start.
    const ee::CameraVisualState result = ee::CalculateCameraVisual(
        &scene, &events, MakePlayback(900.0f, 700.0f), 0.0);
    ExpectNear(result.x, 321.0f, 0.0001f,
        "Player-to-Tile transition must preserve the captured world camera X");
    ExpectNear(result.y, 123.0f, 0.0001f,
        "Player-to-Tile transition must preserve the captured world camera Y");
}

void VerifyReplacementTweenStartsFromCompletedPreviousTarget()
{
    // DOTween DOKill(true) completes the previous channel before the replacement
    // tween is created. The second event therefore starts at the previous target,
    // not at the previous tween's halfway visual position.
    EeCameraEvent first{};
    first.start_time = 0.0;
    first.duration_seconds = 10.0;
    first.start_x = 0.0f;
    first.target_x = 100.0f;
    first.flags = EE_CAMERA_APPLY_X;
    first.ease = EE_CAMERA_EASE_LINEAR;

    EeCameraEvent second{};
    second.start_time = 5.0;
    second.duration_seconds = 10.0;
    second.start_x = 100.0f;
    second.target_x = 200.0f;
    second.flags = EE_CAMERA_APPLY_X;
    second.ease = EE_CAMERA_EASE_LINEAR;

    const std::vector<EeCameraEvent> events{first, second};
    const ee::CameraVisualState at_replacement = ee::CalculateCameraVisual(
        nullptr, &events, MakePlayback(), 5.0);
    ExpectNear(at_replacement.x, 100.0f, 0.0001f,
        "Replacement MoveCamera must start from the completed previous target (DOKill true)");
}
}

int main()
{
    VerifyTileTargetIsFrozenWhenEventStarts();
    VerifyPlayerToTileSwitchPreservesWorldStart();
    VerifyReplacementTweenStartsFromCompletedPreviousTarget();
    std::cout << "PASS: ADOFAI camera rig compatibility regressions are valid.\n";
    return 0;
}
