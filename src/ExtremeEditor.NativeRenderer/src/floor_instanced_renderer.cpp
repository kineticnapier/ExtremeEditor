#include "floor_instanced_renderer.h"
#include "floor_triangulation.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace ee
{
using Microsoft::WRL::ComPtr;

namespace
{
constexpr std::uint32_t TrackColorFlag = 0x80u;

constexpr char ShaderSource[] = R"(
cbuffer FrameConstants : register(b0)
{
    float2 camera;
    float zoom;
    float padding0;
    float2 viewport;
    float edgePixels;
    float padding1;
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
    float2 localScreen = float2(
        c * input.localPosition.x - s * input.localPosition.y,
        -s * input.localPosition.x - c * input.localPosition.y) * zoom;
    float2 centerScreen = float2(
        (input.worldPosition.x - camera.x) * zoom + viewport.x * 0.5,
        (camera.y - input.worldPosition.y) * zoom + viewport.y * 0.5);
    float2 screen = centerScreen + localScreen;
    float2 clip = float2(
        screen.x * 2.0 / viewport.x - 1.0,
        1.0 - screen.y * 2.0 / viewport.y);
    output.position = float4(clip, input.depth, 1.0);
    output.localPosition = input.localPosition;
    output.uv = input.localUv;
    output.color = input.color;
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
    float2 normalPixels = float2(-tangentPixels.y, tangentPixels.x) * edgePixels * 0.5;
    float2 extendPixels = tangentPixels * edgePixels * 0.5;
    float2 normalClip = PixelsToClip(normalPixels);
    float2 extendClip = PixelsToClip(extendPixels);

    VSOutput vertex;
    vertex.localPosition = float2(0.0, 0.0);
    vertex.uv = float2(0.0, 0.0);
    vertex.color = input[0].color;
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

float4 PSTextured(VSOutput input) : SV_Target
{
    float4 sampled = floorTexture.Sample(floorSampler, input.uv);
    clip(sampled.a - (1.0 / 255.0));
    return float4(sampled.rgb * input.color.rgb, sampled.a * input.color.a);
}

float4 PSFallback(VSOutput input) : SV_Target
{
    float directional = saturate(0.5 + input.localPosition.y * 0.45 - input.localPosition.x * 0.08);
    float center = saturate(1.0 - length(input.localPosition) * 0.55);
    float shade = 0.86 + directional * 0.10 + center * 0.08;
    return float4(saturate(drawColor.rgb * input.color.rgb * shade), drawColor.a * input.color.a);
}

float4 PSFlat(VSOutput input) : SV_Target
{
    return drawColor;
}
)";

bool CompileShader(const char* entry, const char* target, ComPtr<ID3DBlob>& bytecode) noexcept
{
    ComPtr<ID3DBlob> errors;
    const UINT flags = D3DCOMPILE_ENABLE_STRICTNESS |
#if defined(_DEBUG)
        D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#else
        D3DCOMPILE_OPTIMIZATION_LEVEL3;
#endif

    return SUCCEEDED(D3DCompile(
        ShaderSource,
        std::strlen(ShaderSource),
        "ExtremeEditorFloorInstancing",
        nullptr,
        nullptr,
        entry,
        target,
        flags,
        0,
        bytecode.GetAddressOf(),
        errors.GetAddressOf()));
}

D3D11_BUFFER_DESC ImmutableBufferDesc(UINT byte_width, UINT bind_flags) noexcept
{
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = byte_width;
    desc.Usage = D3D11_USAGE_IMMUTABLE;
    desc.BindFlags = bind_flags;
    return desc;
}
}

bool FloorInstancedRenderer::Initialize(ID3D11Device* device) noexcept
{
    Shutdown();
    if (device == nullptr)
        return false;

    device_ = device;
    if (!CreatePipeline(device))
        return false;

    // WPF and native rendering deliberately share the same imported floor asset.
    // Texture loading is optional: if the cache is absent we retain the procedural
    // fallback instead of failing renderer initialization.
    if (SUCCEEDED(CoCreateInstance(
            CLSID_WICImagingFactory,
            nullptr,
            CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(wic_factory_.GetAddressOf()))))
    {
        TryLoadDefaultTileTexture();
    }
    return true;
}

void FloorInstancedRenderer::Shutdown() noexcept
{
    geometries_.clear();
    grouped_instances_.clear();
    flat_instances_.clear();
    instance_ranges_.clear();
    instance_capacity_ = 0;
    cached_scene_version_ = std::numeric_limits<std::uint64_t>::max();
    tile_sampler_.Reset();
    tile_texture_.Reset();
    wic_factory_.Reset();
    edge_depth_state_.Reset();
    fill_depth_state_.Reset();
    rasterizer_state_.Reset();
    blend_state_.Reset();
    instance_buffer_.Reset();
    color_constants_.Reset();
    frame_constants_.Reset();
    input_layout_.Reset();
    edge_geometry_shader_.Reset();
    flat_pixel_shader_.Reset();
    fallback_pixel_shader_.Reset();
    textured_pixel_shader_.Reset();
    vertex_shader_.Reset();
    device_.Reset();
}

bool FloorInstancedRenderer::CreatePipeline(ID3D11Device* device) noexcept
{
    ComPtr<ID3DBlob> vertex_bytecode;
    ComPtr<ID3DBlob> textured_bytecode;
    ComPtr<ID3DBlob> fallback_bytecode;
    ComPtr<ID3DBlob> flat_bytecode;
    ComPtr<ID3DBlob> geometry_bytecode;
    if (!CompileShader("VSMain", "vs_5_0", vertex_bytecode) ||
        !CompileShader("PSTextured", "ps_5_0", textured_bytecode) ||
        !CompileShader("PSFallback", "ps_5_0", fallback_bytecode) ||
        !CompileShader("PSFlat", "ps_5_0", flat_bytecode) ||
        !CompileShader("GSEdge", "gs_5_0", geometry_bytecode))
        return false;

    if (FAILED(device->CreateVertexShader(
            vertex_bytecode->GetBufferPointer(),
            vertex_bytecode->GetBufferSize(),
            nullptr,
            vertex_shader_.GetAddressOf())) ||
        FAILED(device->CreatePixelShader(
            textured_bytecode->GetBufferPointer(),
            textured_bytecode->GetBufferSize(),
            nullptr,
            textured_pixel_shader_.GetAddressOf())) ||
        FAILED(device->CreatePixelShader(
            fallback_bytecode->GetBufferPointer(),
            fallback_bytecode->GetBufferSize(),
            nullptr,
            fallback_pixel_shader_.GetAddressOf())) ||
        FAILED(device->CreatePixelShader(
            flat_bytecode->GetBufferPointer(),
            flat_bytecode->GetBufferSize(),
            nullptr,
            flat_pixel_shader_.GetAddressOf())) ||
        FAILED(device->CreateGeometryShader(
            geometry_bytecode->GetBufferPointer(),
            geometry_bytecode->GetBufferSize(),
            nullptr,
            edge_geometry_shader_.GetAddressOf())))
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
    if (FAILED(device->CreateInputLayout(
            elements,
            static_cast<UINT>(std::size(elements)),
            vertex_bytecode->GetBufferPointer(),
            vertex_bytecode->GetBufferSize(),
            input_layout_.GetAddressOf())))
        return false;

    D3D11_BUFFER_DESC constant_desc{};
    constant_desc.ByteWidth = sizeof(FrameConstants);
    constant_desc.Usage = D3D11_USAGE_DEFAULT;
    constant_desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    if (FAILED(device->CreateBuffer(&constant_desc, nullptr, frame_constants_.GetAddressOf())))
        return false;

    constant_desc.ByteWidth = sizeof(ColorConstants);
    if (FAILED(device->CreateBuffer(&constant_desc, nullptr, color_constants_.GetAddressOf())))
        return false;

    D3D11_SAMPLER_DESC sampler_desc{};
    sampler_desc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampler_desc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler_desc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler_desc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler_desc.MinLOD = 0.0f;
    sampler_desc.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device->CreateSamplerState(&sampler_desc, tile_sampler_.GetAddressOf())))
        return false;

    D3D11_BLEND_DESC blend_desc{};
    blend_desc.RenderTarget[0].BlendEnable = TRUE;
    blend_desc.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
    blend_desc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    blend_desc.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    blend_desc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    blend_desc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    blend_desc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    blend_desc.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    if (FAILED(device->CreateBlendState(&blend_desc, blend_state_.GetAddressOf())))
        return false;

    D3D11_RASTERIZER_DESC rasterizer_desc{};
    rasterizer_desc.FillMode = D3D11_FILL_SOLID;
    rasterizer_desc.CullMode = D3D11_CULL_NONE;
    rasterizer_desc.DepthClipEnable = TRUE;
    if (FAILED(device->CreateRasterizerState(&rasterizer_desc, rasterizer_state_.GetAddressOf())))
        return false;

    D3D11_DEPTH_STENCIL_DESC depth_desc{};
    depth_desc.DepthEnable = TRUE;
    depth_desc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
    depth_desc.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
    if (FAILED(device->CreateDepthStencilState(&depth_desc, fill_depth_state_.GetAddressOf())))
        return false;

    depth_desc.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
    if (FAILED(device->CreateDepthStencilState(&depth_desc, edge_depth_state_.GetAddressOf())))
        return false;

    return true;
}

bool FloorInstancedRenderer::TryLoadDefaultTileTexture() noexcept
{
    wchar_t* local_app_data = nullptr;
    std::size_t length = 0;
    if (_wdupenv_s(&local_app_data, &length, L"LOCALAPPDATA") != 0 ||
        local_app_data == nullptr || length <= 1)
    {
        std::free(local_app_data);
        return false;
    }

    std::wstring path(local_app_data);
    std::free(local_app_data);
    path += L"\\ExtremeEditor\\AssetCache\\floor-mesh\\tile.png";
    return LoadTileTexture(path.c_str());
}

bool FloorInstancedRenderer::LoadTileTexture(const wchar_t* path) noexcept
{
    if (path == nullptr || *path == L'\0' || !wic_factory_ || !device_)
        return false;

    ComPtr<IWICBitmapDecoder> decoder;
    if (FAILED(wic_factory_->CreateDecoderFromFilename(
            path,
            nullptr,
            GENERIC_READ,
            WICDecodeMetadataCacheOnLoad,
            decoder.GetAddressOf())))
        return false;

    ComPtr<IWICBitmapFrameDecode> frame;
    if (FAILED(decoder->GetFrame(0, frame.GetAddressOf())))
        return false;

    ComPtr<IWICFormatConverter> converter;
    if (FAILED(wic_factory_->CreateFormatConverter(converter.GetAddressOf())) ||
        FAILED(converter->Initialize(
            frame.Get(),
            GUID_WICPixelFormat32bppRGBA,
            WICBitmapDitherTypeNone,
            nullptr,
            0.0,
            WICBitmapPaletteTypeCustom)))
        return false;

    UINT width = 0;
    UINT height = 0;
    if (FAILED(converter->GetSize(&width, &height)) || width == 0 || height == 0)
        return false;
    if (width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
        height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION || width > UINT_MAX / 4u)
        return false;

    const UINT stride = width * 4u;
    const std::uint64_t byte_count64 = static_cast<std::uint64_t>(stride) * height;
    if (byte_count64 > UINT_MAX)
        return false;
    const UINT byte_count = static_cast<UINT>(byte_count64);

    std::vector<std::uint8_t> pixels(byte_count);
    if (FAILED(converter->CopyPixels(nullptr, stride, byte_count, pixels.data())))
        return false;

    D3D11_TEXTURE2D_DESC texture_desc{};
    texture_desc.Width = width;
    texture_desc.Height = height;
    texture_desc.MipLevels = 1;
    texture_desc.ArraySize = 1;
    texture_desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    texture_desc.SampleDesc.Count = 1;
    texture_desc.Usage = D3D11_USAGE_IMMUTABLE;
    texture_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    D3D11_SUBRESOURCE_DATA texture_data{};
    texture_data.pSysMem = pixels.data();
    texture_data.SysMemPitch = stride;

    ComPtr<ID3D11Texture2D> texture;
    if (FAILED(device_->CreateTexture2D(&texture_desc, &texture_data, texture.GetAddressOf())))
        return false;

    ComPtr<ID3D11ShaderResourceView> view;
    if (FAILED(device_->CreateShaderResourceView(texture.Get(), nullptr, view.GetAddressOf())))
        return false;

    tile_texture_ = std::move(view);
    return true;
}

bool FloorInstancedRenderer::CreateGeometryBuffers(
    ID3D11Device* device,
    const LevelScene& scene,
    const EeGeometry& geometry,
    GeometryBuffers& output) noexcept
{
    if (geometry.point_count < 3)
        return false;

    const EePoint* points = scene.points.data() + geometry.point_offset;
    float min_x = points[0].x;
    float max_x = points[0].x;
    float min_y = points[0].y;
    float max_y = points[0].y;
    for (std::uint32_t i = 1; i < geometry.point_count; ++i)
    {
        min_x = std::min(min_x, points[i].x);
        max_x = std::max(max_x, points[i].x);
        min_y = std::min(min_y, points[i].y);
        max_y = std::max(max_y, points[i].y);
    }

    const float width = std::max(0.000001f, max_x - min_x);
    const float height = std::max(0.000001f, max_y - min_y);
    std::vector<FloorVertex> vertices;
    vertices.reserve(geometry.point_count);
    for (std::uint32_t i = 0; i < geometry.point_count; ++i)
    {
        vertices.push_back(FloorVertex{
            points[i].x,
            points[i].y,
            (points[i].x - min_x) / width,
            (max_y - points[i].y) / height});
    }

    const UINT vertex_bytes = static_cast<UINT>(vertices.size() * sizeof(FloorVertex));
    D3D11_SUBRESOURCE_DATA vertex_data{};
    vertex_data.pSysMem = vertices.data();
    D3D11_BUFFER_DESC vertex_desc = ImmutableBufferDesc(vertex_bytes, D3D11_BIND_VERTEX_BUFFER);
    if (FAILED(device->CreateBuffer(&vertex_desc, &vertex_data, output.vertices.GetAddressOf())))
        return false;

    std::vector<std::uint32_t> fill_indices;
    if (!detail::triangulation::TriangulateSimplePolygon(
            points,
            geometry.point_count,
            fill_indices))
        return false;

    D3D11_SUBRESOURCE_DATA fill_data{};
    fill_data.pSysMem = fill_indices.data();
    D3D11_BUFFER_DESC fill_desc = ImmutableBufferDesc(
        static_cast<UINT>(fill_indices.size() * sizeof(std::uint32_t)),
        D3D11_BIND_INDEX_BUFFER);
    if (FAILED(device->CreateBuffer(&fill_desc, &fill_data, output.fill_indices.GetAddressOf())))
        return false;
    output.fill_index_count = static_cast<std::uint32_t>(fill_indices.size());

    std::vector<std::uint32_t> edge_indices;
    edge_indices.reserve(geometry.point_count * 2u);
    for (std::uint32_t i = 0; i < geometry.point_count; ++i)
    {
        edge_indices.push_back(i);
        edge_indices.push_back((i + 1u) % geometry.point_count);
    }

    D3D11_SUBRESOURCE_DATA edge_data{};
    edge_data.pSysMem = edge_indices.data();
    D3D11_BUFFER_DESC edge_desc = ImmutableBufferDesc(
        static_cast<UINT>(edge_indices.size() * sizeof(std::uint32_t)),
        D3D11_BIND_INDEX_BUFFER);
    if (FAILED(device->CreateBuffer(&edge_desc, &edge_data, output.edge_indices.GetAddressOf())))
        return false;
    output.edge_index_count = static_cast<std::uint32_t>(edge_indices.size());
    return true;
}

bool FloorInstancedRenderer::SyncGeometry(
    const LevelScene& scene,
    std::uint64_t scene_version) noexcept
{
    if (cached_scene_version_ == scene_version)
        return true;
    if (!device_)
        return false;

    std::vector<GeometryBuffers> next;
    next.resize(scene.geometries.size());
    for (std::size_t i = 0; i < scene.geometries.size(); ++i)
    {
        if (!CreateGeometryBuffers(device_.Get(), scene, scene.geometries[i], next[i]))
            return false;
    }

    geometries_ = std::move(next);
    grouped_instances_.clear();
    grouped_instances_.resize(geometries_.size());
    instance_ranges_.clear();
    instance_ranges_.resize(geometries_.size());
    cached_scene_version_ = scene_version;
    return true;
}

bool FloorInstancedRenderer::EnsureInstanceBuffer(
    ID3D11Device* device,
    std::size_t instance_count) noexcept
{
    if (instance_count <= instance_capacity_ && instance_buffer_)
        return true;

    std::size_t next_capacity = 1024;
    while (next_capacity < instance_count)
        next_capacity *= 2;

    if (next_capacity > static_cast<std::size_t>(UINT32_MAX / sizeof(InstanceData)))
        return false;

    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = static_cast<UINT>(next_capacity * sizeof(InstanceData));
    desc.Usage = D3D11_USAGE_DYNAMIC;
    desc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    ComPtr<ID3D11Buffer> next;
    if (FAILED(device->CreateBuffer(&desc, nullptr, next.GetAddressOf())))
        return false;

    instance_buffer_ = std::move(next);
    instance_capacity_ = next_capacity;
    return true;
}

bool FloorInstancedRenderer::UploadInstances(ID3D11DeviceContext* context) noexcept
{
    if (flat_instances_.empty())
        return true;

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context->Map(instance_buffer_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return false;

    std::memcpy(
        mapped.pData,
        flat_instances_.data(),
        flat_instances_.size() * sizeof(InstanceData));
    context->Unmap(instance_buffer_.Get(), 0);
    return true;
}

void FloorInstancedRenderer::SetColor(
    ID3D11DeviceContext* context,
    const ColorConstants& color) noexcept
{
    context->UpdateSubresource(color_constants_.Get(), 0, nullptr, &color, 0, 0);
}

bool FloorInstancedRenderer::Draw(
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
    InstancedFloorDrawStats& stats) noexcept
{
    stats = {};
    if (context == nullptr || render_target == nullptr || depth_target == nullptr ||
        !device_ || !vertex_shader_ || !fallback_pixel_shader_ ||
        !flat_pixel_shader_ || !instance_buffer_ && visible_floors.empty())
    {
        if (visible_floors.empty())
            return true;
        if (!device_)
            return false;
    }

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
        const float color_r = has_track_color
            ? static_cast<float>((floor.icon_flags >> 8) & 0xffu) / 255.0f
            : 1.0f;
        const float color_g = has_track_color
            ? static_cast<float>((floor.icon_flags >> 16) & 0xffu) / 255.0f
            : 1.0f;
        const float color_b = has_track_color
            ? static_cast<float>((floor.icon_flags >> 24) & 0xffu) / 255.0f
            : 1.0f;

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
        std::min<std::size_t>(valid_instances, UINT32_MAX));
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

    const float edge_pixels = std::clamp(zoom * 0.022f, 1.0f, 5.0f);
    const FrameConstants frame{
        camera_x,
        camera_y,
        zoom,
        0.0f,
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_width)),
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_height)),
        edge_pixels,
        0.0f};
    context->UpdateSubresource(frame_constants_.Get(), 0, nullptr, &frame, 0, 0);

    const D3D11_VIEWPORT viewport{
        0.0f,
        0.0f,
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_width)),
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_height)),
        0.0f,
        1.0f};
    context->RSSetViewports(1, &viewport);
    context->RSSetState(rasterizer_state_.Get());
    context->OMSetRenderTargets(1, &render_target, depth_target);
    context->ClearDepthStencilView(depth_target, D3D11_CLEAR_DEPTH, 1.0f, 0);

    const float blend_factor[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    context->OMSetBlendState(blend_state_.Get(), blend_factor, 0xffffffffu);
    context->IASetInputLayout(input_layout_.Get());
    context->VSSetShader(vertex_shader_.Get(), nullptr, 0);
    context->VSSetConstantBuffers(0, 1, frame_constants_.GetAddressOf());
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
    context->GSSetConstantBuffers(0, 1, frame_constants_.GetAddressOf());
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