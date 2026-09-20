#include "icon_instanced_renderer.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

namespace ee
{
using Microsoft::WRL::ComPtr;

namespace
{
constexpr char ShaderSource[] = R"(
cbuffer FrameConstants : register(b0)
{
    float2 camera;
    float zoom;
    float padding0;
    float2 viewport;
    float2 padding1;
};

Texture2D iconTexture : register(t0);
SamplerState iconSampler : register(s0);

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
    float2 local = input.localPosition * input.sizePixels;
    float c = input.rotation.x;
    float s = input.rotation.y;
    float2 offset = float2(
        c * local.x - s * local.y,
        s * local.x + c * local.y);
    float2 center = float2(
        (input.worldPosition.x - camera.x) * zoom + viewport.x * 0.5,
        (camera.y - input.worldPosition.y) * zoom + viewport.y * 0.5);
    float2 screen = center + offset;
    float2 clip = float2(
        screen.x * 2.0 / viewport.x - 1.0,
        1.0 - screen.y * 2.0 / viewport.y);
    output.position = float4(clip, input.depth, 1.0);
    output.uv = input.localUv;
    return output;
}

float4 PSMain(VSOutput input) : SV_Target
{
    float4 sampleColor = iconTexture.Sample(iconSampler, input.uv);
    clip(sampleColor.a - (1.0 / 255.0));
    return sampleColor;
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
        "ExtremeEditorIconInstancing",
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

bool IconInstancedRenderer::Initialize(ID3D11Device* device, IWICImagingFactory* wic_factory) noexcept
{
    Shutdown();
    if (device == nullptr || wic_factory == nullptr)
        return false;

    device_ = device;
    wic_factory_ = wic_factory;
    return CreatePipeline(device);
}

void IconInstancedRenderer::Shutdown() noexcept
{
    assets_.clear();
    batches_.clear();
    flat_instances_.clear();
    instance_capacity_ = 0;
    cached_assets_version_ = std::numeric_limits<std::uint64_t>::max();
    depth_state_.Reset();
    rasterizer_state_.Reset();
    blend_state_.Reset();
    sampler_state_.Reset();
    frame_constants_.Reset();
    instance_buffer_.Reset();
    quad_indices_.Reset();
    quad_vertices_.Reset();
    input_layout_.Reset();
    pixel_shader_.Reset();
    vertex_shader_.Reset();
    wic_factory_.Reset();
    device_.Reset();
}

bool IconInstancedRenderer::CreatePipeline(ID3D11Device* device) noexcept
{
    ComPtr<ID3DBlob> vertex_bytecode;
    ComPtr<ID3DBlob> pixel_bytecode;
    if (!CompileShader("VSMain", "vs_5_0", vertex_bytecode) ||
        !CompileShader("PSMain", "ps_5_0", pixel_bytecode))
    {
        return false;
    }

    if (FAILED(device->CreateVertexShader(
            vertex_bytecode->GetBufferPointer(),
            vertex_bytecode->GetBufferSize(),
            nullptr,
            vertex_shader_.GetAddressOf())) ||
        FAILED(device->CreatePixelShader(
            pixel_bytecode->GetBufferPointer(),
            pixel_bytecode->GetBufferSize(),
            nullptr,
            pixel_shader_.GetAddressOf())))
    {
        return false;
    }

    const D3D11_INPUT_ELEMENT_DESC elements[] =
    {
        {"POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0},
        {"INSTANCEPOS", 0, DXGI_FORMAT_R32G32_FLOAT, 1, 0, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCESIZE", 0, DXGI_FORMAT_R32G32_FLOAT, 1, 8, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCEROT", 0, DXGI_FORMAT_R32G32_FLOAT, 1, 16, D3D11_INPUT_PER_INSTANCE_DATA, 1},
        {"INSTANCEDEPTH", 0, DXGI_FORMAT_R32_FLOAT, 1, 24, D3D11_INPUT_PER_INSTANCE_DATA, 1}
    };
    if (FAILED(device->CreateInputLayout(
            elements,
            static_cast<UINT>(std::size(elements)),
            vertex_bytecode->GetBufferPointer(),
            vertex_bytecode->GetBufferSize(),
            input_layout_.GetAddressOf())))
    {
        return false;
    }

    constexpr QuadVertex quad[] =
    {
        {-0.5f, -0.5f, 0.0f, 0.0f},
        { 0.5f, -0.5f, 1.0f, 0.0f},
        { 0.5f,  0.5f, 1.0f, 1.0f},
        {-0.5f,  0.5f, 0.0f, 1.0f}
    };
    constexpr std::uint16_t indices[] = {0, 1, 2, 0, 2, 3};

    D3D11_BUFFER_DESC vertex_desc{};
    vertex_desc.ByteWidth = sizeof(quad);
    vertex_desc.Usage = D3D11_USAGE_IMMUTABLE;
    vertex_desc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    D3D11_SUBRESOURCE_DATA vertex_data{};
    vertex_data.pSysMem = quad;
    if (FAILED(device->CreateBuffer(&vertex_desc, &vertex_data, quad_vertices_.GetAddressOf())))
        return false;

    D3D11_BUFFER_DESC index_desc{};
    index_desc.ByteWidth = sizeof(indices);
    index_desc.Usage = D3D11_USAGE_IMMUTABLE;
    index_desc.BindFlags = D3D11_BIND_INDEX_BUFFER;
    D3D11_SUBRESOURCE_DATA index_data{};
    index_data.pSysMem = indices;
    if (FAILED(device->CreateBuffer(&index_desc, &index_data, quad_indices_.GetAddressOf())))
        return false;

    D3D11_BUFFER_DESC constant_desc{};
    constant_desc.ByteWidth = sizeof(FrameConstants);
    constant_desc.Usage = D3D11_USAGE_DEFAULT;
    constant_desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    if (FAILED(device->CreateBuffer(&constant_desc, nullptr, frame_constants_.GetAddressOf())))
        return false;

    D3D11_SAMPLER_DESC sampler_desc{};
    sampler_desc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampler_desc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler_desc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler_desc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler_desc.MinLOD = 0.0f;
    sampler_desc.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device->CreateSamplerState(&sampler_desc, sampler_state_.GetAddressOf())))
        return false;

    // WIC loads the icon textures as premultiplied BGRA. Match Direct2D's
    // premultiplied-alpha composition instead of multiplying alpha a second time.
    D3D11_BLEND_DESC blend_desc{};
    blend_desc.RenderTarget[0].BlendEnable = TRUE;
    blend_desc.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
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
    if (FAILED(device->CreateDepthStencilState(&depth_desc, depth_state_.GetAddressOf())))
        return false;

    return true;
}

bool IconInstancedRenderer::LoadTexture(const std::wstring& path, SpriteTexture& output) noexcept
{
    output = {};
    if (path.empty() || !wic_factory_ || !device_)
        return false;

    ComPtr<IWICBitmapDecoder> decoder;
    if (FAILED(wic_factory_->CreateDecoderFromFilename(
            path.c_str(),
            nullptr,
            GENERIC_READ,
            WICDecodeMetadataCacheOnLoad,
            decoder.GetAddressOf())))
    {
        return false;
    }

    ComPtr<IWICBitmapFrameDecode> frame;
    if (FAILED(decoder->GetFrame(0, frame.GetAddressOf())))
        return false;

    ComPtr<IWICFormatConverter> converter;
    if (FAILED(wic_factory_->CreateFormatConverter(converter.GetAddressOf())))
        return false;
    if (FAILED(converter->Initialize(
            frame.Get(),
            GUID_WICPixelFormat32bppPBGRA,
            WICBitmapDitherTypeNone,
            nullptr,
            0.0,
            WICBitmapPaletteTypeCustom)))
    {
        return false;
    }

    UINT width = 0;
    UINT height = 0;
    if (FAILED(converter->GetSize(&width, &height)) || width == 0 || height == 0)
        return false;
    if (width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
        height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
        width > UINT_MAX / 4u)
    {
        return false;
    }

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
    texture_desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    texture_desc.SampleDesc.Count = 1;
    texture_desc.Usage = D3D11_USAGE_IMMUTABLE;
    texture_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    D3D11_SUBRESOURCE_DATA initial_data{};
    initial_data.pSysMem = pixels.data();
    initial_data.SysMemPitch = stride;

    ComPtr<ID3D11Texture2D> texture;
    if (FAILED(device_->CreateTexture2D(&texture_desc, &initial_data, texture.GetAddressOf())))
        return false;
    if (FAILED(device_->CreateShaderResourceView(texture.Get(), nullptr, output.view.GetAddressOf())))
        return false;

    output.width = width;
    output.height = height;
    return true;
}

bool IconInstancedRenderer::SyncAssets(
    const IconAssetTable* assets,
    std::uint64_t assets_version) noexcept
{
    if (cached_assets_version_ == assets_version)
        return true;

    std::unordered_map<std::uint32_t, AssetEntry> next_assets;
    std::vector<Batch> next_batches;

    if (assets != nullptr)
    {
        next_assets.reserve(assets->size());
        next_batches.reserve(assets->size() * 2u);

        for (const auto& [icon_id, asset] : *assets)
        {
            AssetEntry entry;

            SpriteTexture image;
            if (LoadTexture(asset.image_path, image))
            {
                entry.image_batch = static_cast<std::int32_t>(next_batches.size());
                Batch batch;
                batch.texture = std::move(image);
                next_batches.push_back(std::move(batch));
            }

            if (!asset.outline_path.empty())
            {
                SpriteTexture outline;
                if (LoadTexture(asset.outline_path, outline))
                {
                    entry.outline_batch = static_cast<std::int32_t>(next_batches.size());
                    Batch batch;
                    batch.texture = std::move(outline);
                    next_batches.push_back(std::move(batch));
                }
            }

            next_assets.emplace(icon_id, entry);
        }
    }

    assets_ = std::move(next_assets);
    batches_ = std::move(next_batches);
    flat_instances_.clear();
    cached_assets_version_ = assets_version;
    return true;
}

float IconInstancedRenderer::RequestedSize(float requested) noexcept
{
    return std::clamp(requested, 10.0f, 96.0f);
}

bool IconInstancedRenderer::EnsureInstanceBuffer(std::size_t instance_count) noexcept
{
    if (instance_count <= instance_capacity_ && instance_buffer_)
        return true;

    std::size_t next_capacity = 1024;
    while (next_capacity < instance_count)
    {
        if (next_capacity > (std::numeric_limits<std::size_t>::max() / 2u))
            return false;
        next_capacity *= 2u;
    }

    if (next_capacity > static_cast<std::size_t>(UINT_MAX / sizeof(InstanceData)))
        return false;

    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = static_cast<UINT>(next_capacity * sizeof(InstanceData));
    desc.Usage = D3D11_USAGE_DYNAMIC;
    desc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    ComPtr<ID3D11Buffer> next;
    if (FAILED(device_->CreateBuffer(&desc, nullptr, next.GetAddressOf())))
        return false;

    instance_buffer_ = std::move(next);
    instance_capacity_ = next_capacity;
    return true;
}

bool IconInstancedRenderer::UploadInstances(ID3D11DeviceContext* context) noexcept
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

bool IconInstancedRenderer::Draw(
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
    InstancedIconDrawStats& stats) noexcept
{
    stats = {};
    if (context == nullptr || render_target == nullptr || depth_target == nullptr ||
        !device_ || !vertex_shader_ || !pixel_shader_)
    {
        return false;
    }

    if (zoom < 12.0f || assets_.empty() || batches_.empty() || visible_floors.empty())
        return true;

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
                floor.x,
                floor.y,
                draw_width,
                draw_height,
                cosine,
                sine,
                depth});
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
        flat_instances_.insert(
            flat_instances_.end(),
            batch.instances.begin(),
            batch.instances.end());
    }

    if (!UploadInstances(context))
        return false;

    stats.icon_instances = static_cast<std::uint32_t>(
        std::min<std::size_t>(logical_icon_count, UINT_MAX));
    stats.sprite_instances = static_cast<std::uint32_t>(
        std::min<std::size_t>(sprite_count, UINT_MAX));

    const FrameConstants frame{
        camera_x,
        camera_y,
        zoom,
        0.0f,
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_width)),
        static_cast<float>(std::max<std::uint32_t>(1u, viewport_height)),
        0.0f,
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
    context->OMSetDepthStencilState(depth_state_.Get(), 0);

    const float blend_factor[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    context->OMSetBlendState(blend_state_.Get(), blend_factor, 0xffffffffu);
    context->IASetInputLayout(input_layout_.Get());
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->IASetIndexBuffer(quad_indices_.Get(), DXGI_FORMAT_R16_UINT, 0);
    context->VSSetShader(vertex_shader_.Get(), nullptr, 0);
    context->VSSetConstantBuffers(0, 1, frame_constants_.GetAddressOf());
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
