#include "d2d_backend.h"

namespace ee
{
HRESULT D2DBackend::RenderFrame(
    double seconds,
    const LevelScene* scene,
    std::uint64_t scene_version,
    const IconAssetTable* icon_assets,
    std::uint64_t icon_assets_version,
    float camera_x,
    float camera_y,
    float zoom,
    std::int32_t selected_floor,
    const PlaybackVisualState& playback) noexcept
{
    RenderFrameStats ignored;
    return RenderFrame(
        seconds,
        scene,
        scene_version,
        icon_assets,
        icon_assets_version,
        camera_x,
        camera_y,
        zoom,
        selected_floor,
        playback,
        ignored);
}
}
