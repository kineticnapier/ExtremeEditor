#pragma once

#include "d2d_backend.h"

#include <vector>

namespace ee
{
struct CameraVisualState
{
    bool active = false;
    float x = 0.0f;
    float y = 0.0f;
    float zoom_multiplier = 1.0f;
    float rotation = 0.0f;
};

CameraVisualState CalculateCameraVisual(
    const std::vector<EeCameraEvent>* events,
    const PlaybackVisualState& playback,
    double chart_time) noexcept;
}
