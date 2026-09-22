#pragma once

#include "d2d_backend.h"

#include <vector>

namespace ee
{
// ADOFAI stores camera zoom as a view-size multiplier: 200% shows twice as
// much world (it is zoomed OUT), not twice as many pixels per world unit.
// Playback also uses its own 100% scale rather than the editor/Frame All zoom.
struct PlaybackCameraZoom
{
    float multiplier = 1.0f;

    constexpr PlaybackCameraZoom() noexcept = default;
    constexpr PlaybackCameraZoom(float value) noexcept : multiplier(value) {}

    constexpr PlaybackCameraZoom& operator=(float value) noexcept
    {
        multiplier = value;
        return *this;
    }
};

inline float operator*(float /* editor_zoom */, PlaybackCameraZoom camera_zoom) noexcept
{
    constexpr float PlaybackBaseZoom = 56.0f;
    const float safe_multiplier = camera_zoom.multiplier > 0.0001f
        ? camera_zoom.multiplier
        : 0.0001f;
    return PlaybackBaseZoom / safe_multiplier;
}

struct CameraVisualState
{
    bool active = false;
    float x = 0.0f;
    float y = 0.0f;
    PlaybackCameraZoom zoom_multiplier{};
    float rotation = 0.0f;
};

CameraVisualState CalculateCameraVisual(
    const LevelScene* scene,
    const std::vector<EeCameraEvent>* events,
    const PlaybackVisualState& playback,
    double chart_time) noexcept;

CameraVisualState CalculateCameraVisual(
    const std::vector<EeCameraEvent>* events,
    const PlaybackVisualState& playback,
    double chart_time) noexcept;
}
