#include "icon_instanced_renderer.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <cmath>
#include <cstring>

namespace ee
{
using Microsoft::WRL::ComPtr;

namespace
{
constexpr char CameraVertexShaderSource[] = R"(
cbuffer FrameConstants : register(b0)
{
    float2 camera;
    float zoom;
    float padding0;
    float2 viewport;
    float2 padding1;
    float2 cameraRotation;
    float2 padding2;
};

struct VSInput
{
    float2 localPosition : POSITION;
    float2 localUv : TEXCOORD0;
    float2 worldPosition : INSTANCEPOS;
    float2 sizePixels : INSTANCESIZE;
    float2 rotation : INSTANCEROT;
    float depth : INSTANCEDEPTH;
};

struct VSOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    float cc = cameraRotation.x;
    float cs = cameraRotation.y;
    float2 delta = input.worldPosition - camera;
    float2 view = float2(
        cc * delta.x + cs * delta.y,
        -cs * delta.x + cc * delta.y);

    float ic = input.rotation.x;
    float is = input.rotation.y;
    float c = cc * ic - cs * is;
    float s = cs * ic + cc * is;
    float2 local = input.localPosition * input.sizePixels;
    float2 offset = float2(
        c * local.x - s * local.y,
        s * local.x + c * local.y);

    float2 center = float2(
        view.x * zoom + viewport.x * 0.5,
        -view.y * zoom + viewport.y * 0.5);
    float2 screen = center + offset;
    float2 clip = float2(
        screen.x * 2.0 / viewport.x - 1.0,
        1.0 - screen.y * 2.0 / viewport.y);
    output.position = float4(clip, input.depth, 1.0);
    output.uv = input.localUv;
    return output;
}
)";

bool CompileCameraVertexShader(ComPtr<ID3DBlob>& bytecode) noexcept
{
    ComPtr<ID3DBlob> errors;
    const UINT flags = D3DCOMPILE_ENABLE_STRICTNESS |
#if defined(_DEBUG)
        D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#else
        D3DCOMPILE_OPTIMIZATION_LEVEL3;
#endif
    return SUCCEEDED(D3DCompile(
        CameraVertexShaderSource,
        std::strlen(CameraVertexShaderSource),
        "ExtremeEditorIconCamera",
        nullptr,
        nullptr,
        "VSMain",
        "vs_5_0",
        flags,
        0,
        bytecode.GetAddressOf(),
        errors.GetAddressOf()));
}
}

bool IconInstancedRenderer::EnsureCameraPipeline() noexcept
{
    ID3D11Device* current = device_.Get();
    if (current == nullptr)
        return false;
    if (camera_pipeline_device_ == current && camera_vertex_shader_ &&
        camera_input_layout_ && camera_frame_constants_)
        return true;

    camera_vertex_shader_.Reset();
    camera_input_layout_.Reset();
    camera_frame_constants_.Reset();
    camera_pipeline_device_ = nullptr;

    ComPtr<ID3DBlob> bytecode;
    if (!CompileCameraVertexShader(bytecode))
        return false;
    if (FAILED(current->CreateVertexShader(
            bytecode->GetBufferPointer(), bytecode->GetBufferSize(), nullptr,
            camera_vertex_shader_.GetAddressOf())))
        return false;

    const D3D11_INPUT_ELEMENT_DESC elements[] =
    {
        {"POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"INSTANCEPOS", 0, DXGI_FORMAT_R32G32_FLOAT, 1, 0, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCESIZE", 0, DXGI_FORMAT_R32G32_FLOAT, 1, 8, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCEROT", 0, DXGI_FORMAT_R32G32_FLOAT, 1, 16, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCEDEPTH", 0, DXGI_FORMAT_R32_FLOAT, 1, 24, D3D11_INPUT_PER_INSTANCE_DATA, 1}
    };
    if (FAILED(current->CreateInputLayout(
            elements,
            static_cast<UINT>(std::size(elements)),
            bytecode->GetBufferPointer(),
            bytecode->GetBufferSize(),
            camera_input_layout_.GetAddressOf())))
        return false;

    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = sizeof(CameraFrameConstants);
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    if (FAILED(current->CreateBuffer(&desc, nullptr, camera_frame_constants_.GetAddressOf())))
        return false;

    camera_pipeline_device_ = current;
    return true;
}

bool IconInstancedRenderer::DrawCamera(
    ID3D11DeviceContext* context,
    ID3D11RenderTargetView* render_target,
    ID3D11DepthStencilView* depth_target,
    const LevelScene& scene,
    const std::vector<std::uint32_t>& visible_floors,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    std::uint32_t viewport_width,
    std::uint32_t viewport_height,
    InstancedIconDrawStats& stats) noexcept
{
    stats = {};
    if (context == nullptr || render_target == nullptr || depth_target == nullptr ||
        !device_ || !pixel_shader_)
        return false;
    if (zoom < 12.0f || assets_.empty() || batches_.empty() || visible_floors.empty())
        return true;
    if (!EnsureCameraPipeline())
        return false;

    for (Batch& batch : batches_)
    {
        batch.instances.clear();
        batch.range_offset = 0;
    }

    const float depth_step = 1.0f / static_cast<float>(scene.floors.size() + 1u);
    std::size_t sprite_count = 0;
    std::size_t logical_icon_count = 0;

    for (std::uint32_t floor_index : visible_floors)
    {
        if (floor_index >= scene.floors.size())
            continue;
        const EeFloor& floor = scene.floors[floor_index];
        if (floor.icon_id == EE_ICON_NONE)
            continue;

        const auto found = assets_.find(floor.icon_id);
        if (found == assets_.end() || found->second.image_batch < 0)
            continue;

        const AssetEntry& asset = found->second;
        const bool is_floor_icon = (floor.icon_flags & EE_ICON_FLAG_FLOOR) != 0;
        const bool flipped = (floor.icon_flags & EE_ICON_FLAG_FLIPPED) != 0;
        const float base_requested = zoom * (is_floor_icon ? 0.78f : 0.62f);
        const float floor_depth = (static_cast<float>(floor_index) + 1.0f) * depth_step;
        const float cosine = std::cos(floor.icon_angle);
        const float sine = std::sin(floor.icon_angle);

        auto append_instance = [&](std::int32_t batch_index, float requested, float depth_offset) noexcept
        {
            if (batch_index < 0 || static_cast<std::size_t>(batch_index) >= batches_.size())
                return;
            Batch& batch = batches_[static_cast<std::size_t>(batch_index)];
            const std::uint32_t max_dimension = std::max(batch.texture.width, batch.texture.height);
            if (max_dimension == 0)
                return;
            const float size = RequestedSize(requested);
            const float scale = size / static_cast<float>(max_dimension);
            float draw_width = static_cast<float>(batch.texture.width) * scale;
            const float draw_height = static_cast<float>(batch.texture.height) * scale;
            if (flipped)
                draw_width = -draw_width;
            const float depth = std::max(0.0f, floor_depth - depth_step * depth_offset);
            batch.instances.push_back(InstanceData{
                floor.x, floor.y, draw_width, draw_height, cosine, sine, depth});
            ++sprite_count;
        };

        if (is_floor_icon && asset.outline_batch >= 0)
            append_instance(asset.outline_batch, base_requested * 1.04f, 0.20f);
        append_instance(asset.image_batch, base_requested, 0.30f);
        ++logical_icon_count;
    }

    if (sprite_count == 0)
        return true;
    if (!EnsureInstanceBuffer(sprite_count))
        return false;

    flat_instances_.clear();
    flat_instances_.reserve(sprite_count);
    for (Batch& batch : batches_)
    {
        batch.range_offset = static_cast<std::uint32_t>(flat_instances_.size());
        flat_instances_.insert(flat_instances_.end(), batch.instances.begin(), batch.instances.end());
    }
    if (!UploadInstances(context))
        return false;

    stats.icon_instances = static_cast<std::uint32_t>(std::min<std::size_t>(logical_icon_count, UINT_MAX));
    stats.sprite_instances = static_cast<std::uint32_t>(std::min<std::size_t>(sprite_count, UINT_MAX));

    const CameraFrameConstants frame{
        camera_x,
        camera_y,
        zoom,
        0.0f,
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_width)),
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_height)),
        0.0f,
        0.0f,
        std::cos(camera_rotation),
        std::sin(camera_rotation),
        0.0f,
        0.0f};
    context->UpdateSubresource(camera_frame_constants_.Get(), 0, nullptr, &frame, 0, 0);

    const D3D11_VIEWPORT viewport{
        0.0f, 0.0f,
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_width)),
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_height)),
        0.0f, 1.0f};
    context->RSSetViewports(1, &viewport);
    context->RSSetState(rasterizer_state_.Get());
    context->OMSetRenderTargets(1, &render_target, depth_target);
    context->OMSetDepthStencilState(depth_state_.Get(), 0);

    const float blend_factor[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    context->OMSetBlendState(blend_state_.Get(), blend_factor, 0xffffffffu);
    context->IASetInputLayout(camera_input_layout_.Get());
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->IASetIndexBuffer(quad_indices_.Get(), DXGI_FORMAT_R16_UINT, 0);
    context->VSSetShader(camera_vertex_shader_.Get(), nullptr, 0);
    context->VSSetConstantBuffers(0, 1, camera_frame_constants_.GetAddressOf());
    context->PSSetShader(pixel_shader_.Get(), nullptr, 0);
    context->PSSetSamplers(0, 1, sampler_state_.GetAddressOf());

    const UINT vertex_stride = sizeof(QuadVertex);
    const UINT instance_stride = sizeof(InstanceData);
    const UINT vertex_offset = 0;
    for (const Batch& batch : batches_)
    {
        if (batch.instances.empty() || !batch.texture.view)
            continue;
        const UINT instance_offset = batch.range_offset * sizeof(InstanceData);
        ID3D11Buffer* buffers[] = {quad_vertices_.Get(), instance_buffer_.Get()};
        const UINT strides[] = {vertex_stride, instance_stride};
        const UINT offsets[] = {vertex_offset, instance_offset};
        context->IASetVertexBuffers(0, 2, buffers, strides, offsets);
        ID3D11ShaderResourceView* view = batch.texture.view.Get();
        context->PSSetShaderResources(0, 1, &view);
        context->DrawIndexedInstanced(6u, static_cast<UINT>(batch.instances.size()), 0, 0, 0);
        ++stats.draw_calls;
    }

    ID3D11ShaderResourceView* null_view = nullptr;
    context->PSSetShaderResources(0, 1, &null_view);
    context->OMSetDepthStencilState(nullptr, 0);
    context->OMSetBlendState(nullptr, blend_factor, 0xffffffffu);
    return true;
}
}
