#pragma once

#include "level_scene.h"

#include <d3d11.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <cstddef>
#include <cstdint>
#include <iterator>
#include <limits>
#include <vector>

namespace ee
{
struct InstancedFloorDrawStats
{
    std::uint32_t floor_instances = 0;
    std::uint32_t draw_calls = 0;
};

class FloorInstancedRenderer
{
public:
    FloorInstancedRenderer() = default;
    FloorInstancedRenderer(const FloorInstancedRenderer&) = delete;
    FloorInstancedRenderer& operator=(const FloorInstancedRenderer&) = delete;

    bool Initialize(ID3D11Device* device) noexcept;
    void Shutdown() noexcept;
    bool SyncGeometry(const LevelScene& scene, std::uint64_t scene_version) noexcept;

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
        InstancedFloorDrawStats& stats) noexcept;

private:
    struct FloorVertex
    {
        float x;
        float y;
        float u;
        float v;
    };

    struct GeometryBuffers
    {
        Microsoft::WRL::ComPtr<ID3D11Buffer> vertices;
        Microsoft::WRL::ComPtr<ID3D11Buffer> fill_indices;
        Microsoft::WRL::ComPtr<ID3D11Buffer> edge_indices;
        std::uint32_t fill_index_count = 0;
        std::uint32_t edge_index_count = 0;
    };

    struct InstanceData
    {
        float x;
        float y;
        float cosine;
        float sine;
        float depth;
    };

    struct InstanceRange
    {
        std::uint32_t offset = 0;
        std::uint32_t count = 0;
    };

    struct FrameConstants
    {
        float camera_x;
        float camera_y;
        float zoom;
        float padding0;
        float viewport_width;
        float viewport_height;
        float edge_pixels;
        float padding1;
    };

    struct ColorConstants
    {
        float r;
        float g;
        float b;
        float a;
    };

    bool CreatePipeline(ID3D11Device* device) noexcept;
    bool TryLoadDefaultTileTexture() noexcept;
    bool LoadTileTexture(const wchar_t* path) noexcept;
    bool EnsureInstanceBuffer(ID3D11Device* device, std::size_t instance_count) noexcept;
    bool CreateGeometryBuffers(
        ID3D11Device* device,
        const LevelScene& scene,
        const EeGeometry& geometry,
        GeometryBuffers& output) noexcept;
    bool UploadInstances(ID3D11DeviceContext* context) noexcept;
    void SetColor(ID3D11DeviceContext* context, const ColorConstants& color) noexcept;

    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<IWICImagingFactory> wic_factory_;
    Microsoft::WRL::ComPtr<ID3D11VertexShader> vertex_shader_;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> textured_pixel_shader_;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> fallback_pixel_shader_;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> flat_pixel_shader_;
    Microsoft::WRL::ComPtr<ID3D11GeometryShader> edge_geometry_shader_;
    Microsoft::WRL::ComPtr<ID3D11InputLayout> input_layout_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> frame_constants_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> color_constants_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> instance_buffer_;
    Microsoft::WRL::ComPtr<ID3D11BlendState> blend_state_;
    Microsoft::WRL::ComPtr<ID3D11RasterizerState> rasterizer_state_;
    Microsoft::WRL::ComPtr<ID3D11DepthStencilState> fill_depth_state_;
    Microsoft::WRL::ComPtr<ID3D11DepthStencilState> edge_depth_state_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> tile_texture_;
    Microsoft::WRL::ComPtr<ID3D11SamplerState> tile_sampler_;

    std::vector<GeometryBuffers> geometries_;
    std::vector<std::vector<InstanceData>> grouped_instances_;
    std::vector<InstanceData> flat_instances_;
    std::vector<InstanceRange> instance_ranges_;
    std::size_t instance_capacity_ = 0;
    std::uint64_t cached_scene_version_ = std::numeric_limits<std::uint64_t>::max();
};
}