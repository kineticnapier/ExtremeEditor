#pragma once

#include "d2d_backend.h"

#include <vector>

namespace ee
{
// ADOFAI playback camera zoom is relative to the game's fixed 100% camera scale,
// not to whatever editor zoom/Frame All scale was active before playback.  Keep
// the existing renderer expression `editor_zoom * camera_zoom` source-compatible,
// but make that product resolve against ExtremeEditor's stock 100% scale (28 px
// per world unit).  This also means stopping playback naturally returns to the
// untouched editor zoom stored by Renderer.
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
    constexpr float PlaybackBaseZoom = 28.0f;
    return PlaybackBaseZoom * camera_zoom.multiplier;
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
    const std::vector<EeCameraEvent>* events,
    const PlaybackVisualState& playback,
    double chart_time) noexcept;
}
