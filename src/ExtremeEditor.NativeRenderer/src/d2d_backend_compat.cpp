#include "d2d_backend.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <mutex>

namespace ee
{
namespace
{
std::mutex latest_render_stats_mutex;
RenderFrameStats latest_render_stats;

void PublishRenderFrameStats(const RenderFrameStats& stats) noexcept
{
    std::lock_guard lock(latest_render_stats_mutex);
    latest_render_stats = stats;
}
}

RenderFrameStats GetLatestRenderFrameStats() noexcept
{
    std::lock_guard lock(latest_render_stats_mutex);
    return latest_render_stats;
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
    {
        PublishRenderFrameStats(stats);
        return E_FAIL;
    }

    const auto setup_started = std::chrono::steady_clock::now();
    if (!SyncSceneGeometry(scene, scene_version))
    {
        PublishRenderFrameStats(stats);
        return E_FAIL;
    }
    if (scene != nullptr && !floor_renderer_.SyncGeometry(*scene, scene_version))
    {
        PublishRenderFrameStats(stats);
        return E_FAIL;
    }
    SyncIconAssets(icon_assets_version);
    if (!icon_renderer_.SyncAssets(icon_assets, icon_assets_version))
    {
        PublishRenderFrameStats(stats);
        return E_FAIL;
    }

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
    const auto setup_finished = std::chrono::steady_clock::now();
    stats.draw_setup_ms = std::chrono::duration<double, std::milli>(
        setup_finished - setup_started).count();

    const auto first_end_draw_started = std::chrono::steady_clock::now();
    HRESULT hr = EndD2DDraw();
    const auto first_end_draw_finished = std::chrono::steady_clock::now();
    stats.end_draw_ms += std::chrono::duration<double, std::milli>(
        first_end_draw_finished - first_end_draw_started).count();
    if (hr == S_FALSE)
    {
        PublishRenderFrameStats(stats);
        return S_OK;
    }
    if (FAILED(hr))
    {
        PublishRenderFrameStats(stats);
        return hr;
    }

    if (scene != nullptr)
    {
        QueryVisibleFloorsCamera(*scene, camera_x, camera_y, zoom, camera_rotation, stats);

        // RenderFrame receives a const view of the shared scene, but runtime track
        // transforms are mutable render state. Position transforms were already
        // advanced by Renderer::RenderLoop; only visual-only transforms for the
        // culled candidates are materialized here.
        const auto visual_update_started = std::chrono::steady_clock::now();
        const_cast<LevelScene*>(scene)->EvaluateVisibleTrackVisuals(visible_candidates_);
        const auto visual_update_finished = std::chrono::steady_clock::now();
        stats.update_ms = std::chrono::duration<double, std::milli>(
            visual_update_finished - visual_update_started).count();

        const auto floor_started = std::chrono::steady_clock::now();
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
        {
            PublishRenderFrameStats(stats);
            return E_FAIL;
        }
        const auto floor_finished = std::chrono::steady_clock::now();
        stats.floor_ms = std::chrono::duration<double, std::milli>(
            floor_finished - floor_started).count();
        stats.floor_draws = floor_stats.floor_instances;
        stats.draw_calls += floor_stats.draw_calls;

        const auto icon_started = std::chrono::steady_clock::now();
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
        {
            PublishRenderFrameStats(stats);
            return E_FAIL;
        }
        const auto icon_finished = std::chrono::steady_clock::now();
        stats.icon_ms = std::chrono::duration<double, std::milli>(
            icon_finished - icon_started).count();
        stats.icon_draws = icon_stats.icon_instances;
        stats.draw_calls += icon_stats.draw_calls;
    }

    const auto overlay_started = std::chrono::steady_clock::now();
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
    const auto overlay_finished = std::chrono::steady_clock::now();
    stats.overlay_ms = std::chrono::duration<double, std::milli>(
        overlay_finished - overlay_started).count();

    const auto second_end_draw_started = std::chrono::steady_clock::now();
    hr = EndD2DDraw();
    const auto second_end_draw_finished = std::chrono::steady_clock::now();
    stats.end_draw_ms += std::chrono::duration<double, std::milli>(
        second_end_draw_finished - second_end_draw_started).count();
    if (hr == S_FALSE)
    {
        PublishRenderFrameStats(stats);
        return S_OK;
    }
    if (FAILED(hr))
    {
        PublishRenderFrameStats(stats);
        return hr;
    }

    const auto present_started = std::chrono::steady_clock::now();
    hr = swap_chain_->Present(1, 0);
    const auto present_finished = std::chrono::steady_clock::now();
    stats.present_ms = std::chrono::duration<double, std::milli>(
        present_finished - present_started).count();
    PublishRenderFrameStats(stats);
    return hr;
}
}
