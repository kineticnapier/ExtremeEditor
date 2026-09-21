#include "d2d_backend.h"

#include <algorithm>
#include <cmath>

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
    return RenderFrame(
        seconds,
        scene,
        scene_version,
        icon_assets,
        icon_assets_version,
        camera_x,
        camera_y,
        zoom,
        0.0f,
        selected_floor,
        playback);
}

HRESULT D2DBackend::RenderFrame(
    double seconds,
    const LevelScene* scene,
    std::uint64_t scene_version,
    const IconAssetTable* icon_assets,
    std::uint64_t icon_assets_version,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    std::int32_t selected_floor,
    const PlaybackVisualState& playback) noexcept
{
    RenderFrameStats stats{};
    if (!d2d_context_ || !swap_chain_ || !target_bitmap_ ||
        !render_target_view_ || !depth_stencil_view_)
        return E_FAIL;

    if (!SyncSceneGeometry(scene, scene_version))
        return E_FAIL;
    if (scene != nullptr && !floor_renderer_.SyncGeometry(*scene, scene_version))
        return E_FAIL;
    SyncIconAssets(icon_assets_version);
    if (!icon_renderer_.SyncAssets(icon_assets, icon_assets_version))
        return E_FAIL;

    d2d_context_->BeginDraw();
    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
    d2d_context_->Clear(D2D1::ColorF(0x14161A));

    constexpr float grid_step = 64.0f;
    for (float x = 0.0f; x <= static_cast<float>(width_); x += grid_step)
    {
        d2d_context_->DrawLine(
            D2D1::Point2F(x, 0.0f),
            D2D1::Point2F(x, static_cast<float>(height_)),
            grid_brush_.Get(),
            1.0f);
        ++stats.draw_calls;
    }
    for (float y = 0.0f; y <= static_cast<float>(height_); y += grid_step)
    {
        d2d_context_->DrawLine(
            D2D1::Point2F(0.0f, y),
            D2D1::Point2F(static_cast<float>(width_), y),
            grid_brush_.Get(),
            1.0f);
        ++stats.draw_calls;
    }

    HRESULT hr = EndD2DDraw();
    if (hr == S_FALSE)
        return S_OK;
    if (FAILED(hr))
        return hr;

    if (scene != nullptr)
    {
        QueryVisibleFloorsCamera(*scene, camera_x, camera_y, zoom, camera_rotation, stats);

        InstancedFloorDrawStats floor_stats;
        if (!floor_renderer_.DrawCamera(
                d3d_context_.Get(),
                render_target_view_.Get(),
                depth_stencil_view_.Get(),
                *scene,
                visible_candidates_,
                camera_x,
                camera_y,
                std::clamp(zoom, 0.05f, 400.0f),
                camera_rotation,
                width_,
                height_,
                floor_stats))
            return E_FAIL;

        stats.floor_draws = floor_stats.floor_instances;
        stats.draw_calls += floor_stats.draw_calls;

        InstancedIconDrawStats icon_stats;
        if (!icon_renderer_.DrawCamera(
                d3d_context_.Get(),
                render_target_view_.Get(),
                depth_stencil_view_.Get(),
                *scene,
                visible_candidates_,
                camera_x,
                camera_y,
                std::clamp(zoom, 0.05f, 400.0f),
                camera_rotation,
                width_,
                height_,
                icon_stats))
            return E_FAIL;

        stats.icon_draws = icon_stats.icon_instances;
        stats.draw_calls += icon_stats.draw_calls;
    }

    d2d_context_->BeginDraw();
    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());

    if (scene != nullptr)
    {
        DrawSceneOverlaysCamera(
            *scene,
            nullptr,
            camera_x,
            camera_y,
            zoom,
            camera_rotation,
            selected_floor,
            stats);

        if (!playback.active)
            DrawEditorHud(*scene, camera_x, camera_y, zoom, selected_floor, stats);

        DrawPlaybackPlanetsCamera(playback, camera_x, camera_y, zoom, camera_rotation);
        if (playback.active)
            stats.draw_calls += 4u;
    }
    else
    {
        const float pulse = 0.5f + 0.5f * static_cast<float>(std::sin(seconds * 2.0));
        const float radius = 28.0f + 10.0f * pulse;
        const D2D1_POINT_2F center = D2D1::Point2F(
            static_cast<float>(width_) * 0.5f,
            static_cast<float>(height_) * 0.5f);
        d2d_context_->FillEllipse(D2D1::Ellipse(center, radius, radius), accent_brush_.Get());
        ++stats.draw_calls;
    }

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
    const D2D1_RECT_F border = D2D1::RectF(
        0.5f,
        0.5f,
        std::max(1.0f, static_cast<float>(width_) - 0.5f),
        std::max(1.0f, static_cast<float>(height_) - 0.5f));
    d2d_context_->DrawRectangle(border, border_brush_.Get(), 1.0f);
    ++stats.draw_calls;

    hr = EndD2DDraw();
    if (hr == S_FALSE)
        return S_OK;
    if (FAILED(hr))
        return hr;

    return swap_chain_->Present(1, 0);
}
}
