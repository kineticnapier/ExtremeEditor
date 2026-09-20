#pragma once

#include "icon_assets.h"
#include "level_scene.h"

#include <d3d11.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <cstddef>
#include <cstdint>
#include <limits>
#include <unordered_map>
#include <vector>

namespace ee
{
struct InstancedIconDrawStats
{
    std::uint32_t icon_instances = 0;
    std::uint32_t sprite_instances = 0;
    std::uint32_t draw_calls = 0;
};

class IconInstancedRenderer
{
public:
    IconInstancedRenderer() = default;
    IconInstancedRenderer(const IconInstancedRenderer&) = delete;
    IconInstancedRenderer& operator=(const IconInstancedRenderer&) = delete;

    bool Initialize(ID3D11Device* device, IWICImagingFactory* wic_factory) noexcept;
    void Shutdown() noexcept;
    bool SyncAssets(const IconAssetTable* assets, std::uint64_t assets_version) noexcept;

    bool Draw(
        ID3D11DeviceContext* context,
        ID3D11RenderTargetView* render_target,
        ID3D11DepthStencilView* depth_target,
        const LevelScene& scene,
        const std::vector<std::uint32_t>& visible_floors,
        float camera_x,
        float camera_y,
        float zoom,
        std::uint32_t viewport_width,
        std::uint32_t viewport_height,
        InstancedIconDrawStats& stats) noexcept;

private:
    struct SpriteTexture
    {
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> view;
        std::uint32_t width = 0;
        std::uint32_t height = 0;
    };

    struct Batch
    {
        SpriteTexture texture;
        std::vector<struct InstanceData> instances;
        std::uint32_t range_offset = 0;
    };

    struct AssetEntry
    {
        std::int32_t image_batch = -1;
        std::int32_t outline_batch = -1;
    };

    struct QuadVertex
    {
        float x;
        float y;
        float u;
        float v;
    };

    struct InstanceData
    {
        float x;
        float y;
        float width;
        float height;
        float cosine;
        float sine;
        float depth;
    };

    struct FrameConstants
    {
        float camera_x;
        float camera_y;
        float zoom;
        float padding0;
        float viewport_width;
        float viewport_height;
        float padding1;
        float padding2;
    };

    bool CreatePipeline(ID3D11Device* device) noexcept;
    bool LoadTexture(const std::wstring& path, SpriteTexture& output) noexcept;
    bool EnsureInstanceBuffer(std::size_t instance_count) noexcept;
    bool UploadInstances(ID3D11DeviceContext* context) noexcept;
    static float RequestedSize(float requested) noexcept;

    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<IWICImagingFactory> wic_factory_;
    Microsoft::WRL::ComPtr<ID3D11VertexShader> vertex_shader_;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> pixel_shader_;
    Microsoft::WRL::ComPtr<ID3D11InputLayout> input_layout_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> quad_vertices_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> quad_indices_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> instance_buffer_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> frame_constants_;
    Microsoft::WRL::ComPtr<ID3D11SamplerState> sampler_state_;
    Microsoft::WRL::ComPtr<ID3D11BlendState> blend_state_;
    Microsoft::WRL::ComPtr<ID3D11RasterizerState> rasterizer_state_;
    Microsoft::WRL::ComPtr<ID3D11DepthStencilState> depth_state_;

    std::unordered_map<std::uint32_t, AssetEntry> assets_;
    std::vector<Batch> batches_;
    std::vector<InstanceData> flat_instances_;
    std::size_t instance_capacity_ = 0;
    std::uint64_t cached_assets_version_ = std::numeric_limits<std::uint64_t>::max();
};
}
