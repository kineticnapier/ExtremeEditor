#pragma once

#include "level_scene.h"

#include <windows.h>
#include <d2d1_1.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstdint>
#include <limits>
#include <vector>

namespace ee
{
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
        float camera_x,
        float camera_y,
        float zoom) noexcept;

private:
    bool CreateDeviceResources(HWND hwnd, std::uint32_t width, std::uint32_t height) noexcept;
    bool CreateTargetBitmap() noexcept;
    void ReleaseTargetBitmap() noexcept;
    bool SyncSceneGeometry(const LevelScene* scene, std::uint64_t scene_version) noexcept;
    void DrawScene(const LevelScene& scene, float camera_x, float camera_y, float zoom) noexcept;

    std::uint32_t width_ = 1;
    std::uint32_t height_ = 1;

    Microsoft::WRL::ComPtr<ID3D11Device> d3d_device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> d3d_context_;
    Microsoft::WRL::ComPtr<IDXGISwapChain1> swap_chain_;

    Microsoft::WRL::ComPtr<ID2D1Factory1> d2d_factory_;
    Microsoft::WRL::ComPtr<ID2D1Device> d2d_device_;
    Microsoft::WRL::ComPtr<ID2D1DeviceContext> d2d_context_;
    Microsoft::WRL::ComPtr<ID2D1Bitmap1> target_bitmap_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> grid_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> accent_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> border_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> floor_brush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> floor_edge_brush_;

    std::vector<Microsoft::WRL::ComPtr<ID2D1PathGeometry>> floor_geometries_;
    std::vector<std::uint32_t> visible_candidates_;
    std::uint64_t cached_scene_version_ = std::numeric_limits<std::uint64_t>::max();
};
}
