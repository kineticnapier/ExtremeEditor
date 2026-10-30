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
        "Completed Tile MoveCamera target Y must not follow later MoveTrack movement");
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

void VerifyPlayerTweenStaysInMovingPlayerFrame()
{
    // Keylimit's later section contains long Player-relative MoveCamera tweens.
    // In ADOFAI the camera offset tween lives in the Player reference frame, so
    // player motion translates BOTH endpoints. Blending from a fixed world-space
    // start makes the camera resist/argue against the player until the tween ends.
    EeCameraEvent item{};
    item.start_time = 0.0;
    item.duration_seconds = 10.0;
    item.start_x = 0.0f;   // Player-relative offset at StartEffect.
    item.target_x = 10.0f; // Player-relative target offset.
    item.flags = EE_CAMERA_APPLY_X | EE_CAMERA_TARGET_PLAYER_X;
    item.ease = EE_CAMERA_EASE_LINEAR;

    const std::vector<EeCameraEvent> events{item};

    // Halfway through the offset tween the smoothed Player pivot has moved to X=120.
    // Expected world camera: 120 + lerp(0, 10, 0.5) = 125.
    const ee::CameraVisualState halfway = ee::CalculateCameraVisual(
        nullptr, &events, MakePlayback(120.0f, 0.0f), 5.0);
    ExpectNear(halfway.x, 125.0f, 0.0001f,
        "RED: Player MoveCamera must tween its offset inside the moving Player frame");
}
}

void VerifyIndexedCameraParity()
{
    std::vector<EeCameraEvent> events;
    events.reserve(10000);
    for (int i = 0; i < 10000; ++i)
    {
        EeCameraEvent item{};
        item.start_time = static_cast<double>(i / 3);
        item.duration_seconds = 5.0;
        item.start_x = static_cast<float>(i % 100);
        item.target_x = item.start_x + 10.0f;
        item.start_y = static_cast<float>(i % 75);
        item.target_y = item.start_y - 4.0f;
        item.start_rotation = static_cast<float>(i % 360);
        item.target_rotation = item.start_rotation + 30.0f;
        item.start_zoom = 1.0f;
        item.target_zoom = 2.0f;
        item.ease = EE_CAMERA_EASE_LINEAR;
        item.flags = i == 0
            ? EE_CAMERA_APPLY_X | EE_CAMERA_APPLY_Y | EE_CAMERA_APPLY_ROTATION | EE_CAMERA_APPLY_ZOOM
            : i % 101 == 0 ? EE_CAMERA_APPLY_ZOOM
            : i % 37 == 0 ? EE_CAMERA_APPLY_ROTATION
            : i % 3 == 0 ? EE_CAMERA_APPLY_Y : EE_CAMERA_APPLY_X;
        if (i % 29 == 0)
            item.flags |= EE_CAMERA_TARGET_PLAYER_X;
        events.push_back(item);
    }

    const auto lookup = ee::CameraComponentLookup::Build(events,
        {EE_CAMERA_APPLY_X, EE_CAMERA_APPLY_Y,
         EE_CAMERA_APPLY_ROTATION, EE_CAMERA_APPLY_ZOOM});
    const auto playback = MakePlayback(12.0f, -7.0f);
    for (double time : {-1.0, 0.0, 1.0, 20.5, 500.0, 3333.0, 99999.0})
    {
        const auto original = ee::CalculateCameraVisual(
            nullptr, &events, playback, time);
        const auto indexed = ee::CalculateCameraVisual(
            nullptr, &events, playback, time, &lookup);
        ExpectNear(indexed.x, original.x, 0.0001f, "Indexed X must match original");
        ExpectNear(indexed.y, original.y, 0.0001f, "Indexed Y must match original");
        ExpectNear(indexed.rotation, original.rotation, 0.0001f,
            "Indexed rotation must match original");
        ExpectNear(indexed.zoom_multiplier.multiplier, original.zoom_multiplier.multiplier,
            0.0001f, "Indexed zoom must match original");
    }
}

int main()
{
    VerifyTileTargetIsFrozenWhenEventStarts();
    VerifyPlayerToTileSwitchPreservesWorldStart();
    VerifyReplacementTweenStartsFromCompletedPreviousTarget();
    VerifyPlayerTweenStaysInMovingPlayerFrame();
    VerifyIndexedCameraParity();
    std::cout << "PASS: ADOFAI camera rig compatibility regressions are valid.\n";
    return 0;
}
