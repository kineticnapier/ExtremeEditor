#include "floor_instanced_renderer.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <cmath>
#include <cstring>

namespace ee
{
using Microsoft::WRL::ComPtr;

namespace
{
constexpr std::uint32_t TrackColorFlag = 0x80u;

constexpr char CameraVertexShaderSource[] = R"(
cbuffer FrameConstants : register(b0)
{
    float2 camera;
    float zoom;
    float padding0;
    float2 viewport;
    float edgePixels;
    float padding1;
    float2 cameraRotation;
    float2 padding2;
};

struct VSInput
{
    float2 localPosition : POSITION;
    float2 localUv : TEXCOORD0;
    float2 worldPosition : INSTANCEPOS;
    float2 rotation : INSTANCEROT;
    float depth : INSTANCEDEPTH;
    float4 color : INSTANCECOLOR;
};

struct VSOutput
{
    float4 position : SV_Position;
    float2 localPosition : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 color : COLOR0;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    float c = input.rotation.x;
    float s = input.rotation.y;
    float2 localWorld = float2(
        c * input.localPosition.x - s * input.localPosition.y,
        s * input.localPosition.x + c * input.localPosition.y);
    float2 delta = input.worldPosition - camera + localWorld;
    float cc = cameraRotation.x;
    float cs = cameraRotation.y;
    float2 view = float2(
        cc * delta.x + cs * delta.y,
        -cs * delta.x + cc * delta.y);
    float2 screen = float2(
        view.x * zoom + viewport.x * 0.5,
        -view.y * zoom + viewport.y * 0.5);
    float2 clip = float2(
        screen.x * 2.0 / viewport.x - 1.0,
        1.0 - screen.y * 2.0 / viewport.y);
    output.position = float4(clip, input.depth, 1.0);
    output.localPosition = input.localPosition;
    output.uv = input.localUv;
    output.color = input.color;
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
        "ExtremeEditorFloorCamera",
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

bool FloorInstancedRenderer::EnsureCameraPipeline() noexcept
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
        {"INSTANCEROT", 0, DXGI_FORMAT_R32G32_FLOAT, 1, 8, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCEDEPTH", 0, DXGI_FORMAT_R32_FLOAT, 1, 16, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCECOLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 20, D3D11_INPUT_PER_INSTANCE_DATA, 1}
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

bool FloorInstancedRenderer::DrawCamera(
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
    InstancedFloorDrawStats& stats) noexcept
{
    stats = {};
    if (context == nullptr || render_target == nullptr || depth_target == nullptr ||
        !device_ || !fallback_pixel_shader_ || !flat_pixel_shader_)
        return false;
    if (visible_floors.empty())
        return true;
    if (!EnsureCameraPipeline())
        return false;
    if (geometries_.size() != scene.geometries.size())
        return false;

    for (auto& group : grouped_instances_)
        group.clear();

    const float depth_denominator = static_cast<float>(scene.floors.size() + 1u);
    std::size_t valid_instances = 0;
    for (std::uint32_t floor_index : visible_floors)
    {
        if (floor_index >= scene.floors.size())
            continue;
        const EeFloor& floor = scene.floors[floor_index];
        if (floor.geometry_id >= grouped_instances_.size())
            continue;

        const bool has_track_color = (floor.icon_flags & TrackColorFlag) != 0u;
        const float color_r = has_track_color ? static_cast<float>((floor.icon_flags >> 8) & 0xffu) / 255.0f : 1.0f;
        const float color_g = has_track_color ? static_cast<float>((floor.icon_flags >> 16) & 0xffu) / 255.0f : 1.0f;
        const float color_b = has_track_color ? static_cast<float>((floor.icon_flags >> 24) & 0xffu) / 255.0f : 1.0f;

        grouped_instances_[floor.geometry_id].push_back(InstanceData{
            floor.x,
            floor.y,
            std::cos(floor.entry_angle),
            std::sin(floor.entry_angle),
            (static_cast<float>(floor_index) + 1.0f) / depth_denominator,
            color_r,
            color_g,
            color_b,
            1.0f});
        ++valid_instances;
    }

    stats.floor_instances = static_cast<std::uint32_t>(
        std::min<std::size_t>(valid_instances, UINT_MAX));
    if (valid_instances == 0)
        return true;
    if (!EnsureInstanceBuffer(device_.Get(), valid_instances))
        return false;

    flat_instances_.clear();
    flat_instances_.reserve(valid_instances);
    for (std::size_t geometry_id = 0; geometry_id < grouped_instances_.size(); ++geometry_id)
    {
        InstanceRange& range = instance_ranges_[geometry_id];
        range.offset = static_cast<std::uint32_t>(flat_instances_.size());
        range.count = static_cast<std::uint32_t>(grouped_instances_[geometry_id].size());
        flat_instances_.insert(
            flat_instances_.end(),
            grouped_instances_[geometry_id].begin(),
            grouped_instances_[geometry_id].end());
    }
    if (!UploadInstances(context))
        return false;

    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float edge_pixels = std::clamp(zoom * 0.022f, 1.0f, 5.0f);
    const CameraFrameConstants frame{
        camera_x,
        camera_y,
        zoom,
        0.0f,
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_width)),
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_height)),
        edge_pixels,
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
    context->ClearDepthStencilView(depth_target, D3D11_CLEAR_DEPTH, 1.0f, 0);

    const float blend_factor[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    context->OMSetBlendState(blend_state_.Get(), blend_factor, 0xffffffffu);
    context->IASetInputLayout(camera_input_layout_.Get());
    context->VSSetShader(camera_vertex_shader_.Get(), nullptr, 0);
    context->VSSetConstantBuffers(0, 1, camera_frame_constants_.GetAddressOf());
    context->PSSetConstantBuffers(1, 1, color_constants_.GetAddressOf());

    const UINT vertex_stride = sizeof(FloorVertex);
    const UINT instance_stride = sizeof(InstanceData);
    const UINT vertex_offset = 0;

    context->GSSetShader(nullptr, nullptr, 0);
    context->OMSetDepthStencilState(fill_depth_state_.Get(), 0);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

    if (tile_texture_ && textured_pixel_shader_)
    {
        context->PSSetShader(textured_pixel_shader_.Get(), nullptr, 0);
        ID3D11ShaderResourceView* view = tile_texture_.Get();
        ID3D11SamplerState* sampler = tile_sampler_.Get();
        context->PSSetShaderResources(0, 1, &view);
        context->PSSetSamplers(0, 1, &sampler);
    }
    else
    {
        SetColor(context, ColorConstants{1.0f, 1.0f, 1.0f, 0.94f});
        context->PSSetShader(fallback_pixel_shader_.Get(), nullptr, 0);
    }

    for (std::size_t geometry_id = 0; geometry_id < geometries_.size(); ++geometry_id)
    {
        const InstanceRange& range = instance_ranges_[geometry_id];
        if (range.count == 0)
            continue;
        const GeometryBuffers& geometry = geometries_[geometry_id];
        const UINT instance_offset = range.offset * sizeof(InstanceData);
        ID3D11Buffer* buffers[] = {geometry.vertices.Get(), instance_buffer_.Get()};
        const UINT strides[] = {vertex_stride, instance_stride};
        const UINT offsets[] = {vertex_offset, instance_offset};
        context->IASetVertexBuffers(0, 2, buffers, strides, offsets);
        context->IASetIndexBuffer(geometry.fill_indices.Get(), DXGI_FORMAT_R32_UINT, 0);
        context->DrawIndexedInstanced(geometry.fill_index_count, range.count, 0, 0, 0);
        ++stats.draw_calls;
    }

    ID3D11ShaderResourceView* no_view = nullptr;
    context->PSSetShaderResources(0, 1, &no_view);
    SetColor(context, ColorConstants{24.0f / 255.0f, 22.0f / 255.0f, 18.0f / 255.0f, 0.48f});
    context->PSSetShader(flat_pixel_shader_.Get(), nullptr, 0);
    context->GSSetShader(edge_geometry_shader_.Get(), nullptr, 0);
    context->GSSetConstantBuffers(0, 1, camera_frame_constants_.GetAddressOf());
    context->OMSetDepthStencilState(edge_depth_state_.Get(), 0);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);

    for (std::size_t geometry_id = 0; geometry_id < geometries_.size(); ++geometry_id)
    {
        const InstanceRange& range = instance_ranges_[geometry_id];
        if (range.count == 0)
            continue;
        const GeometryBuffers& geometry = geometries_[geometry_id];
        const UINT instance_offset = range.offset * sizeof(InstanceData);
        ID3D11Buffer* buffers[] = {geometry.vertices.Get(), instance_buffer_.Get()};
        const UINT strides[] = {vertex_stride, instance_stride};
        const UINT offsets[] = {vertex_offset, instance_offset};
        context->IASetVertexBuffers(0, 2, buffers, strides, offsets);
        context->IASetIndexBuffer(geometry.edge_indices.Get(), DXGI_FORMAT_R32_UINT, 0);
        context->DrawIndexedInstanced(geometry.edge_index_count, range.count, 0, 0, 0);
        ++stats.draw_calls;
    }

    context->GSSetShader(nullptr, nullptr, 0);
    context->OMSetDepthStencilState(nullptr, 0);
    context->OMSetBlendState(nullptr, blend_factor, 0xffffffffu);
    return true;
}
}
