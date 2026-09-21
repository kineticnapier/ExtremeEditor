#pragma once

#include "floor_instanced_renderer.h"
#include "icon_assets.h"
#include "icon_instanced_renderer.h"
#include "level_scene.h"

#include <windows.h>
#include <d2d1_1.h>
#include <d3d11.h>
#include <dwrite.h>
#include <dxgi1_2.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <cstdint>
#include <limits>
#include <string>
#include <unordered_map>
#include <vector>

namespace ee
{
struct PlaybackVisualState
{
    bool active = false;
    std::int32_t floor = -1;
    float stationary_x = 0.0f;
    float stationary_y = 0.0f;
    float orbiting_x = 0.0f;
    float orbiting_y = 0.0f;
    bool stationary_is_red = true;
};

struct RenderFrameStats
{
    double cull_ms = 0.0;
    std::uint32_t visible_candidates = 0;
    std::uint32_t floor_draws = 0;
    std::uint32_t icon_draws = 0;
    std::uint32_t draw_calls = 0;
};

class D2DBackend
{
public:
    D2DBackend() = default;
    D2DBackend(const D2DBackend&) = delete;
    D2DBackend& operator=(const D2DBackend&) = delete;
    ~D2DBackend();

    bool Initialize(HWND hwnd, std::uint32_t width, std::uint32_t height) noexcept;
    void Shutdown() noexcept;
    bool Resize(std::uint32_t width, std::uint32_t height) noexcept;
    HRESULT RenderFrame(
        double seconds,
        const LevelScene* scene,
        std::uint64_t scene_version,
        const IconAssetTable* icon_assets,
        std::uint64_t icon_assets_version,
        float camera_x,
        float camera_y,
        float zoom,
        std::int32_t selected_floor,
        const PlaybackVisualState& playback) noexcept;
    HRESULT RenderFrame(
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
        const PlaybackVisualState& playback) noexcept;
    HRESULT RenderFrame(
        double seconds,
        const LevelScene* scene,
        std::uint64_t scene_version,
        const IconAssetTable* icon_assets,
        std::uint64_t icon_assets_version,
        float camera_x,
        float camera_y,
        float zoom,
        std::int32_t selected_floor,
        const PlaybackVisualState& playback,
        RenderFrameStats& stats) noexcept;

private:
    struct IconBitmapSet
    {
        Microsoft::WRL::ComPtr<ID2D1Bitmap1> image;
        Microsoft::WRL::ComPtr<ID2D1Bitmap1> outline;
        bool attempted = false;
    };

    bool CreateDeviceResources(HWND hwnd, std::uint32_t width, std::uint32_t height) noexcept;
    bool CreateTargetBitmap() noexcept;
    void ReleaseTargetBitmap() noexcept;
    bool SyncSceneGeometry(const LevelScene* scene, std::uint64_t scene_version) noexcept;
    void SyncIconAssets(std::uint64_t icon_assets_version) noexcept;
    Microsoft::WRL::ComPtr<ID2D1Bitmap1> LoadBitmap(const std::wstring& path) noexcept;
    IconBitmapSet* GetIconBitmaps(std::uint32_t icon_id, const IconAssetTable* icon_assets) noexcept;
    void DrawBitmapCentered(
        ID2D1Bitmap1* bitmap,
        float center_x,
        float center_y,
        float requested_size,
        float angle_radians,
        bool flipped) noexcept;
    void QueryVisibleFloors(
        const LevelScene& scene,
        float camera_x,
        float camera_y,
        float zoom,
        RenderFrameStats& stats) noexcept;
    void QueryVisibleFloorsCamera(
        const LevelScene& scene,
        float camera_x,
        float camera_y,
        float zoom,
        float camera_rotation,
        RenderFrameStats& stats) noexcept;
    void DrawSceneOverlays(
        const LevelScene& scene,
        const IconAssetTable* icon_assets,
        float camera_x,
        float camera_y,
        float zoom,
        std::int32_t selected_floor,
        RenderFrameStats& stats) noexcept;
    void DrawSceneOverlaysCamera(
        const LevelScene& scene,
        const IconAssetTable* icon_assets,
        float camera_x,
        float camera_y,
        float zoom,
        float camera_rotation,
        std::int32_t selected_floor,
        RenderFrameStats& stats) noexcept;
    void DrawEditorHud(
        const LevelScene& scene,
        float camera_x,
        float camera_y,
        float zoom,
        std::int32_t selected_floor,
        RenderFrameStats& stats) noexcept;
    void DrawPlaybackPlanets(
        const PlaybackVisualState& playback,
        float camera_x,
        float camera_y,
        float zoom) noexcept;
    void DrawPlaybackPlanetsCamera(
        const PlaybackVisualState& playback,
        float camera_x,
        float camera_y,
        float zoom,
        float camera_rotation) noexcept;
    HRESULT EndD2DDraw() noexcept;

    std::uint32_t width_ = 1;
    std::uint32_t height_ = 1;

    Microsoft::WRL::ComPtr<ID3D11Device> d3d_device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> d3d_context_;
    Microsoft::WRL::ComPtr<IDXGISwapChain1> swap_chain_;
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> render_target_view_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> depth_texture_;
    Microsoft::WRL::ComPtr<ID3D11DepthStencilView> depth_stencil_view_;

    Microsoft::WRL::ComPtr<ID2D1Factory1> d2d_factory_;
    Microsoft::WRL::ComPtr<ID2D1Device> d2d_device_;
    Microsoft::WRL::ComPtr<ID2D1DeviceContext> d2d_context_;
    Microsoft::WRL::ComPtr<ID2D1Bitmap1> target_bitmap_;
    Microsoft::WRL::ComPtr<IWICImagingFactory> wic_factory_;
    Microsoft::WRL::ComPtr<IDWriteFactory> dwrite_factory_;
    Microsoft::WRL::ComPtr<IDWriteTextFormat> hud_text_format_;
    Microsoft::WRL::ComPtr<IDWriteTextFormat> hud_subtext_format_;
    Microsoft::WRL::ComPtr<IDWriteTextFormat> hud_center_format_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> grid_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> accent_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> border_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> floor_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> floor_edge_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> selection_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> planet_red_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> planet_blue_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> planet_outline_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> hud_button_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> hud_destructive_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> hud_accent_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> hud_text_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> hud_subtext_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> hud_center_brush_;

    FloorInstancedRenderer floor_renderer_;
    IconInstancedRenderer icon_renderer_;
    std::vector<Microsoft::WRL::ComPtr<ID2D1PathGeometry>> floor_geometries_;
    std::vector<std::uint32_t> visible_candidates_;
    std::unordered_map<std::uint32_t, IconBitmapSet> icon_bitmaps_;
    std::uint64_t cached_scene_version_ = std::numeric_limits<std::uint64_t>::max();
    std::uint64_t cached_icon_assets_version_ = std::numeric_limits<std::uint64_t>::max();
};
}
