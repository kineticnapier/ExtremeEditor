#include "d2d_backend.h"
#include "track_visual.h"

#include <algorithm>
#include <cstdint>
#include <cstring>

namespace ee
{
using Microsoft::WRL::ComPtr;

bool D2DBackend::InitializeOffscreen(std::uint32_t width, std::uint32_t height) noexcept
{
    Shutdown();
    offscreen_texture_.Reset();
    staging_texture_.Reset();

    width_ = std::max<std::uint32_t>(1u, width);
    height_ = std::max<std::uint32_t>(1u, height);

    UINT device_flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
#if defined(_DEBUG)
    device_flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif

    D3D_FEATURE_LEVEL feature_level{};
    HRESULT hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        device_flags,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        d3d_device_.GetAddressOf(),
        &feature_level,
        d3d_context_.GetAddressOf());

    if (FAILED(hr))
    {
        device_flags &= ~D3D11_CREATE_DEVICE_DEBUG;
        hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            device_flags,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            d3d_device_.GetAddressOf(),
            &feature_level,
            d3d_context_.GetAddressOf());
    }
    if (FAILED(hr))
        return false;

    ComPtr<IDXGIDevice> dxgi_device;
    if (FAILED(d3d_device_.As(&dxgi_device)))
        return false;

    D2D1_FACTORY_OPTIONS factory_options{};
    hr = D2D1CreateFactory(
        D2D1_FACTORY_TYPE_SINGLE_THREADED,
        __uuidof(ID2D1Factory1),
        &factory_options,
        reinterpret_cast<void**>(d2d_factory_.GetAddressOf()));
    if (FAILED(hr))
        return false;

    hr = d2d_factory_->CreateDevice(dxgi_device.Get(), d2d_device_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d2d_device_->CreateDeviceContext(
        D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
        d2d_context_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = CoCreateInstance(
        CLSID_WICImagingFactory,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(wic_factory_.GetAddressOf()));
    if (FAILED(hr))
        return false;

    if (!CreateOffscreenTargetBitmap())
        return false;

    hr = d2d_context_->CreateSolidColorBrush(
        D2D1::ColorF(0xEB4848),
        planet_red_brush_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d2d_context_->CreateSolidColorBrush(
        D2D1::ColorF(0x488EEB),
        planet_blue_brush_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d2d_context_->CreateSolidColorBrush(
        D2D1::ColorF(0xF5F5FA, 0.86f),
        planet_outline_brush_.GetAddressOf());
    if (FAILED(hr))
        return false;

    if (!floor_renderer_.Initialize(d3d_device_.Get()))
        return false;
    return icon_renderer_.Initialize(d3d_device_.Get(), wic_factory_.Get());
}

bool D2DBackend::CreateOffscreenTargetBitmap() noexcept
{
    if (!d3d_device_ || !d2d_context_)
        return false;

    ReleaseTargetBitmap();
    offscreen_texture_.Reset();
    staging_texture_.Reset();

    D3D11_TEXTURE2D_DESC color_desc{};
    color_desc.Width = width_;
    color_desc.Height = height_;
    color_desc.MipLevels = 1;
    color_desc.ArraySize = 1;
    color_desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    color_desc.SampleDesc.Count = 1;
    color_desc.Usage = D3D11_USAGE_DEFAULT;
    color_desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

    HRESULT hr = d3d_device_->CreateTexture2D(
        &color_desc,
        nullptr,
        offscreen_texture_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d3d_device_->CreateRenderTargetView(
        offscreen_texture_.Get(),
        nullptr,
        render_target_view_.GetAddressOf());
    if (FAILED(hr))
        return false;

    D3D11_TEXTURE2D_DESC depth_desc{};
    depth_desc.Width = width_;
    depth_desc.Height = height_;
    depth_desc.MipLevels = 1;
    depth_desc.ArraySize = 1;
    depth_desc.Format = DXGI_FORMAT_D24_UNORM_S8_UINT;
    depth_desc.SampleDesc.Count = 1;
    depth_desc.Usage = D3D11_USAGE_DEFAULT;
    depth_desc.BindFlags = D3D11_BIND_DEPTH_STENCIL;
    hr = d3d_device_->CreateTexture2D(
        &depth_desc,
        nullptr,
        depth_texture_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d3d_device_->CreateDepthStencilView(
        depth_texture_.Get(),
        nullptr,
        depth_stencil_view_.GetAddressOf());
    if (FAILED(hr))
        return false;

    ComPtr<IDXGISurface> surface;
    hr = offscreen_texture_.As(&surface);
    if (FAILED(hr))
        return false;

    const D2D1_BITMAP_PROPERTIES1 properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE),
        96.0f,
        96.0f);

    hr = d2d_context_->CreateBitmapFromDxgiSurface(
        surface.Get(),
        &properties,
        target_bitmap_.GetAddressOf());
    if (FAILED(hr))
        return false;

    d2d_context_->SetTarget(target_bitmap_.Get());
    return true;
}

bool D2DBackend::ResizeOffscreen(std::uint32_t width, std::uint32_t height) noexcept
{
    if (!d3d_device_ || !d2d_context_ || !offscreen_texture_)
        return false;

    width_ = std::max<std::uint32_t>(1u, width);
    height_ = std::max<std::uint32_t>(1u, height);
    return CreateOffscreenTargetBitmap();
}

HRESULT D2DBackend::RenderFrameOffscreen(
    double visual_time,
    const LevelScene* scene,
    std::uint64_t scene_version,
    const IconAssetTable* icon_assets,
    std::uint64_t icon_assets_version,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    const PlaybackVisualState& playback) noexcept
{
    if (!d2d_context_ || !offscreen_texture_ || !target_bitmap_ ||
        !render_target_view_ || !depth_stencil_view_ || scene == nullptr)
        return E_FAIL;

    const ScopedTrackVisualTime visual_time_scope(visual_time);

    if (!SyncSceneGeometry(scene, scene_version))
        return E_FAIL;
    if (!floor_renderer_.SyncGeometry(*scene, scene_version))
        return E_FAIL;
    SyncIconAssets(icon_assets_version);
    if (!icon_renderer_.SyncAssets(icon_assets, icon_assets_version))
        return E_FAIL;

    DecorationRenderState decoration_state = scene->GetDecorationRenderState();
    if (!SyncStaticDecorations(
            decoration_state.decorations.get(),
            decoration_state.decorations_version,
            decoration_state.assets.get(),
            decoration_state.assets_version,
            scene->generation))
        return E_FAIL;

    // B0 headless frames intentionally omit editor-only grid, selection, HUD,
    // and viewport border. The scene itself is rendered by the same production
    // floor/icon/decoration/planet pipelines as the interactive viewport.
    d2d_context_->BeginDraw();
    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
    d2d_context_->Clear(D2D1::ColorF(0x14161A));
    HRESULT hr = d2d_context_->EndDraw();
    if (FAILED(hr))
        return hr;

    RenderFrameStats stats{};
    QueryVisibleFloorsCamera(*scene, camera_x, camera_y, zoom, camera_rotation, stats);
    const_cast<LevelScene*>(scene)->EvaluateVisibleTrackVisuals(visible_candidates_);

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
        return E_FAIL;

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
        return E_FAIL;

    d2d_context_->BeginDraw();
    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
    DrawStaticDecorationsCamera(
        *scene,
        playback,
        camera_x,
        camera_y,
        zoom,
        camera_rotation,
        stats);
    DrawPlaybackPlanetsCamera(playback, camera_x, camera_y, zoom, camera_rotation);
    hr = d2d_context_->EndDraw();
    if (FAILED(hr))
        return hr;

    return S_OK;
}

bool D2DBackend::ReadbackRgb(
    std::uint8_t* rgb,
    std::uint32_t rgb_size,
    std::uint32_t row_stride) noexcept
{
    if (rgb == nullptr || !d3d_device_ || !d3d_context_ || !offscreen_texture_)
        return false;

    const std::uint64_t packed_stride = static_cast<std::uint64_t>(width_) * 3u;
    if (row_stride < packed_stride)
        return false;
    const std::uint64_t required = static_cast<std::uint64_t>(row_stride) * height_;
    if (required > rgb_size)
        return false;

    if (!staging_texture_)
    {
        D3D11_TEXTURE2D_DESC desc{};
        offscreen_texture_->GetDesc(&desc);
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;
        if (FAILED(d3d_device_->CreateTexture2D(
                &desc,
                nullptr,
                staging_texture_.GetAddressOf())))
            return false;
    }

    d3d_context_->CopyResource(staging_texture_.Get(), offscreen_texture_.Get());

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(d3d_context_->Map(
            staging_texture_.Get(),
            0,
            D3D11_MAP_READ,
            0,
            &mapped)))
        return false;

    const auto* source_base = static_cast<const std::uint8_t*>(mapped.pData);
    for (std::uint32_t y = 0; y < height_; ++y)
    {
        const std::uint8_t* source = source_base + static_cast<std::size_t>(y) * mapped.RowPitch;
        std::uint8_t* destination = rgb + static_cast<std::size_t>(y) * row_stride;
        for (std::uint32_t x = 0; x < width_; ++x)
        {
            destination[x * 3u + 0u] = source[x * 4u + 2u];
            destination[x * 3u + 1u] = source[x * 4u + 1u];
            destination[x * 3u + 2u] = source[x * 4u + 0u];
        }
    }

    d3d_context_->Unmap(staging_texture_.Get(), 0);
    return true;
}
}
