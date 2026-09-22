#include "floor_instanced_renderer.h"
#include "track_visual.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <cmath>
#include <cstring>

namespace ee
{
using Microsoft::WRL::ComPtr;

namespace
{
constexpr char CameraShaderSource[] = R"(
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

cbuffer ColorConstants : register(b1)
{
    float4 drawColor;
};

Texture2D floorTexture : register(t0);
SamplerState floorSampler : register(s0);

struct VSInput
{
    float2 localPosition : POSITION;
    float2 localUv : TEXCOORD0;
    float2 worldPosition : INSTANCEPOS;
    float2 rotation : INSTANCEROT;
    float depth : INSTANCEDEPTH;
    float4 color : INSTANCECOLOR;
    // xy = floor scale, z = opacity, w reserved.
    float4 transform : INSTANCETRANSFORM;
};

struct VSOutput
{
    float4 position : SV_Position;
    float2 localPosition : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 color : COLOR0;
    float opacity : TEXCOORD2;
};

float TrackStyle(float payload)
{
    return floor(payload + 0.0001);
}

float TrackGlow(float payload)
{
    return saturate(frac(payload));
}

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    float c = input.rotation.x;
    float s = input.rotation.y;
    float2 scaledLocal = input.localPosition * input.transform.xy;
    float2 localWorld = float2(
        c * scaledLocal.x - s * scaledLocal.y,
        s * scaledLocal.x + c * scaledLocal.y);
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
    output.opacity = saturate(input.transform.z);
    return output;
}

float2 PixelsToClip(float2 pixels)
{
    return float2(
        pixels.x * 2.0 / viewport.x,
        -pixels.y * 2.0 / viewport.y);
}

[maxvertexcount(4)]
void GSEdge(line VSOutput input[2], inout TriangleStream<VSOutput> stream)
{
    float2 a = input[0].position.xy / input[0].position.w;
    float2 b = input[1].position.xy / input[1].position.w;
    float2 deltaPixels = float2(
        (b.x - a.x) * viewport.x * 0.5,
        -(b.y - a.y) * viewport.y * 0.5);
    float lengthPixels = max(length(deltaPixels), 0.0001);
    float2 tangentPixels = deltaPixels / lengthPixels;
    float style = TrackStyle(input[0].color.a);
    float glow = TrackGlow(input[0].color.a);
    float styleWidth = (style >= 0.5 && style < 2.5) ? (1.0 + glow * 1.5) : 1.0;
    float2 normalPixels = float2(-tangentPixels.y, tangentPixels.x) * edgePixels * styleWidth * 0.5;
    float2 extendPixels = tangentPixels * edgePixels * styleWidth * 0.5;
    float2 normalClip = PixelsToClip(normalPixels);
    float2 extendClip = PixelsToClip(extendPixels);

    VSOutput vertex;
    vertex.localPosition = float2(0.0, 0.0);
    vertex.uv = float2(0.0, 0.0);
    vertex.color = input[0].color;
    vertex.opacity = min(input[0].opacity, input[1].opacity);
    vertex.position = float4(a - extendClip + normalClip, input[0].position.z, 1.0);
    stream.Append(vertex);
    vertex.position = float4(a - extendClip - normalClip, input[0].position.z, 1.0);
    stream.Append(vertex);
    vertex.position = float4(b + extendClip + normalClip, input[1].position.z, 1.0);
    stream.Append(vertex);
    vertex.position = float4(b + extendClip - normalClip, input[1].position.z, 1.0);
    stream.Append(vertex);
    stream.RestartStrip();
}

float4 TrackFill(float3 tint, float alpha, float2 localPosition, float3 textured)
{
    float style = TrackStyle(alpha);
    if (style < 0.5) return float4(textured * tint, 1.0);
    if (style < 1.5) return float4(tint * 0.018, 1.0);
    if (style < 2.5) return float4(tint * 0.48, 1.0);
    if (style < 3.5) return float4(tint, 1.0);
    if (style < 4.5) return float4(tint, 1.0);
    float gem = saturate(0.82 + localPosition.y * 0.12 - localPosition.x * 0.04);
    return float4(saturate(tint * gem), 1.0);
}

float4 PSTextured(VSOutput input) : SV_Target
{
    float4 sampled = floorTexture.Sample(floorSampler, input.uv);
    clip(sampled.a * input.opacity - (1.0 / 255.0));
    float4 result = TrackFill(input.color.rgb, input.color.a, input.localPosition, sampled.rgb);
    result.a = sampled.a * input.opacity;
    return result;
}

float4 PSFallback(VSOutput input) : SV_Target
{
    float directional = saturate(0.5 + input.localPosition.y * 0.45 - input.localPosition.x * 0.08);
    float center = saturate(1.0 - length(input.localPosition) * 0.55);
    float shade = 0.86 + directional * 0.10 + center * 0.08;
    float3 procedural = saturate(drawColor.rgb * shade);
    float4 result = TrackFill(input.color.rgb, input.color.a, input.localPosition, procedural);
    result.a = drawColor.a * input.opacity;
    return result;
}

float4 PSFlat(VSOutput input) : SV_Target
{
    float style = TrackStyle(input.color.a);
    float glow = TrackGlow(input.color.a);
    float3 tint = input.color.rgb;
    float3 edge;
    float alpha = 0.62;

    if (style < 0.5 && glow < 0.001 && all(tint > 0.999))
    {
        float4 legacy = drawColor;
        legacy.a *= input.opacity;
        return legacy;
    }

    if (style < 0.5)
        edge = tint * 0.30;
    else if (style < 1.5)
    {
        edge = saturate(tint * (0.82 + glow * 0.45));
        alpha = 0.72 + glow * 0.24;
    }
    else if (style < 2.5)
    {
        edge = saturate(tint * (0.88 + glow * 0.35));
        alpha = 0.76 + glow * 0.20;
    }
    else if (style < 3.5)
        edge = float3(0.025, 0.025, 0.025);
    else if (style < 4.5)
        edge = tint;
    else
        edge = tint * 0.28;

    return float4(saturate(edge), saturate(alpha) * input.opacity);
}
)";

bool CompileCameraShader(
    const char* entry,
    const char* target,
    ComPtr<ID3DBlob>& bytecode) noexcept
{
    ComPtr<ID3DBlob> errors;
    const UINT flags = D3DCOMPILE_ENABLE_STRICTNESS |
#if defined(_DEBUG)
        D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#else
        D3DCOMPILE_OPTIMIZATION_LEVEL3;
#endif

    return SUCCEEDED(D3DCompile(
        CameraShaderSource,
        std::strlen(CameraShaderSource),
        "ExtremeEditorFloorCameraTransform",
        nullptr,
        nullptr,
        entry,
        target,
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
        camera_textured_pixel_shader_ && camera_fallback_pixel_shader_ &&
        camera_flat_pixel_shader_ && camera_edge_geometry_shader_ &&
        camera_input_layout_ && camera_frame_constants_)
        return true;

    camera_vertex_shader_.Reset();
    camera_textured_pixel_shader_.Reset();
    camera_fallback_pixel_shader_.Reset();
    camera_flat_pixel_shader_.Reset();
    camera_edge_geometry_shader_.Reset();
    camera_input_layout_.Reset();
    camera_frame_constants_.Reset();
    camera_pipeline_device_ = nullptr;

    ComPtr<ID3DBlob> vertex_bytecode;
    ComPtr<ID3DBlob> textured_bytecode;
    ComPtr<ID3DBlob> fallback_bytecode;
    ComPtr<ID3DBlob> flat_bytecode;
    ComPtr<ID3DBlob> edge_bytecode;
    if (!CompileCameraShader("VSMain", "vs_5_0", vertex_bytecode) ||
        !CompileCameraShader("PSTextured", "ps_5_0", textured_bytecode) ||
        !CompileCameraShader("PSFallback", "ps_5_0", fallback_bytecode) ||
        !CompileCameraShader("PSFlat", "ps_5_0", flat_bytecode) ||
        !CompileCameraShader("GSEdge", "gs_5_0", edge_bytecode))
        return false;

    if (FAILED(current->CreateVertexShader(
            vertex_bytecode->GetBufferPointer(), vertex_bytecode->GetBufferSize(), nullptr,
            camera_vertex_shader_.GetAddressOf())) ||
        FAILED(current->CreatePixelShader(
            textured_bytecode->GetBufferPointer(), textured_bytecode->GetBufferSize(), nullptr,
            camera_textured_pixel_shader_.GetAddressOf())) ||
        FAILED(current->CreatePixelShader(
            fallback_bytecode->GetBufferPointer(), fallback_bytecode->GetBufferSize(), nullptr,
            camera_fallback_pixel_shader_.GetAddressOf())) ||
        FAILED(current->CreatePixelShader(
            flat_bytecode->GetBufferPointer(), flat_bytecode->GetBufferSize(), nullptr,
            camera_flat_pixel_shader_.GetAddressOf())) ||
        FAILED(current->CreateGeometryShader(
            edge_bytecode->GetBufferPointer(), edge_bytecode->GetBufferSize(), nullptr,
            camera_edge_geometry_shader_.GetAddressOf())))
        return false;

    const D3D11_INPUT_ELEMENT_DESC elements[] =
    {
        {"POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"INSTANCEPOS", 0, DXGI_FORMAT_R32G32_FLOAT, 1, 0, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCEROT", 0, DXGI_FORMAT_R32G32_FLOAT, 1, 8, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCEDEPTH", 0, DXGI_FORMAT_R32_FLOAT, 1, 16, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCECOLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 20, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCETRANSFORM", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 1, 36, D3D11_INPUT_PER_INSTANCE_DATA, 1}
    };
    if (FAILED(current->CreateInputLayout(
            elements,
            static_cast<UINT>(std::size(elements)),
            vertex_bytecode->GetBufferPointer(),
            vertex_bytecode->GetBufferSize(),
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
    if (context == nullptr || render_target == nullptr || depth_target == nullptr || !device_)
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
    const float track_time = TrackVisualTimeSeconds();
    std::size_t valid_instances = 0;
    for (std::uint32_t floor_index : visible_floors)
    {
        if (floor_index >= scene.floors.size())
            continue;
        const EeFloor& floor = scene.floors[floor_index];
        if (floor.geometry_id >= grouped_instances_.size())
            continue;

        const bool transformed = (floor.track_transform_flags & EE_TRACK_TRANSFORM_ENABLED) != 0u;
        const float scale_x = transformed ? floor.transform_scale_x : 1.0f;
        const float scale_y = transformed ? floor.transform_scale_y : 1.0f;
        const float opacity = transformed ? floor.transform_opacity : 1.0f;
        if (opacity <= 0.0001f || std::abs(scale_x) <= 0.0001f || std::abs(scale_y) <= 0.0001f)
            continue;

        const ResolvedTrackVisual visual = ResolveTrackVisual(floor, floor_index, track_time);
        grouped_instances_[floor.geometry_id].push_back(InstanceData{
            floor.x,
            floor.y,
            std::cos(floor.entry_angle),
            std::sin(floor.entry_angle),
            (static_cast<float>(floor_index) + 1.0f) / depth_denominator,
            visual.r,
            visual.g,
            visual.b,
            visual.style_glow,
            scale_x,
            scale_y,
            opacity,
            0.0f});
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

    if (tile_texture_ && camera_textured_pixel_shader_)
    {
        context->PSSetShader(camera_textured_pixel_shader_.Get(), nullptr, 0);
        ID3D11ShaderResourceView* view = tile_texture_.Get();
        ID3D11SamplerState* sampler = tile_sampler_.Get();
        context->PSSetShaderResources(0, 1, &view);
        context->PSSetSamplers(0, 1, &sampler);
    }
    else
    {
        SetColor(context, ColorConstants{1.0f, 1.0f, 1.0f, 0.94f});
        context->PSSetShader(camera_fallback_pixel_shader_.Get(), nullptr, 0);
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
    context->PSSetShader(camera_flat_pixel_shader_.Get(), nullptr, 0);
    context->GSSetShader(camera_edge_geometry_shader_.Get(), nullptr, 0);
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
