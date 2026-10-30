#include "renderer_camera.h"
#include "level_scene.h"

namespace ee
{
CameraVisualState CalculateCameraVisual(
    const std::vector<EeCameraEvent>* events,
    const PlaybackVisualState& playback,
    double chart_time,
    const CameraComponentLookup* lookup) noexcept
{
    return CalculateCameraVisual(
        LevelScene::CurrentRuntimeScene(),
        events,
        playback,
        chart_time,
        lookup);
}
}
