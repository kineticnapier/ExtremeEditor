#include "d2d_backend.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <functional>

namespace ee
{
using Microsoft::WRL::ComPtr;

D2DBackend::~D2DBackend()
{
    Shutdown();
}

bool D2DBackend::Initialize(HWND hwnd, std::uint32_t width, std::uint32_t height) noexcept
{
    Shutdown();
    width_ = std::max<std::uint32_t>(1u, width);
    height_ = std::max<std::uint32_t>(1u, height);
    return CreateDeviceResources(hwnd, width_, height_);
}

void D2DBackend::Shutdown() noexcept
{
    icon_renderer_.Shutdown();
    floor_renderer_.Shutdown();
    icon_bitmaps_.clear();
    floor_geometries_.clear();
    visible_candidates_.clear();
    cached_scene_version_ = std::numeric_limits<std::uint64_t>::max();
    cached_icon_assets_version_ = std::numeric_limits<std::uint64_t>::max();
    ReleaseTargetBitmap();
    planet_outline_brush_.Reset();
    planet_blue_brush_.Reset();
    planet_red_brush_.Reset();
    selection_brush_.Reset();
    floor_edge_brush_.Reset();
    floor_brush_.Reset();
    border_brush_.Reset();
    accent_brush_.Reset();
    grid_brush_.Reset();
    wic_factory_.Reset();
    d2d_context_.Reset();
    d2d_device_.Reset();
    d2d_factory_.Reset();
    swap_chain_.Reset();
    d3d_context_.Reset();
    d3d_device_.Reset();
}

bool D2DBackend::CreateDeviceResources(HWND hwnd, std::uint32_t width, std::uint32_t height) noexcept
{
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

    ComPtr<IDXGIAdapter> adapter;
    if (FAILED(dxgi_device->GetAdapter(adapter.GetAddressOf())))
        return false;

    ComPtr<IDXGIFactory2> dxgi_factory;
    if (FAILED(adapter->GetParent(IID_PPV_ARGS(dxgi_factory.GetAddressOf()))))
        return false;

    DXGI_SWAP_CHAIN_DESC1 swap_desc{};
    swap_desc.Width = width;
    swap_desc.Height = height;
    swap_desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    swap_desc.SampleDesc.Count = 1;
    swap_desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swap_desc.BufferCount = 2;
    swap_desc.Scaling = DXGI_SCALING_STRETCH;
    swap_desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    swap_desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

    hr = dxgi_factory->CreateSwapChainForHwnd(
        d3d_device_.Get(),
        hwnd,
        &swap_desc,
        nullptr,
        nullptr,
        swap_chain_.GetAddressOf());
    if (FAILED(hr))
        return false;

    dxgi_factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);

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

    if (!CreateTargetBitmap())
        return false;

    hr = d2d_context_->CreateSolidColorBrush(
        D2D1::ColorF(0x2A2F38, 0.75f),
        grid_brush_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d2d_context_->CreateSolidColorBrush(
        D2D1::ColorF(0x66D9EF),
        accent_brush_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d2d_context_->CreateSolidColorBrush(
        D2D1::ColorF(0xA8B0BD),
        border_brush_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d2d_context_->CreateSolidColorBrush(
        D2D1::ColorF(0xE1E4EB, 0.94f),
        floor_brush_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d2d_context_->CreateSolidColorBrush(
        D2D1::ColorF(0x181612, 0.48f),
        floor_edge_brush_.GetAddressOf());
    if (FAILED(hr))
        return false;

    hr = d2d_context_->CreateSolidColorBrush(
        D2D1::ColorF(0xFFD250),
        selection_brush_.GetAddressOf());
    if (FAILED(hr))
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

bool D2DBackend::CreateTargetBitmap() noexcept
{
    ComPtr<ID3D11Texture2D> back_buffer;
    HRESULT hr = swap_chain_->GetBuffer(0, IID_PPV_ARGS(back_buffer.GetAddressOf()));
    if (FAILED(hr))
        return false;

    hr = d3d_device_->CreateRenderTargetView(
        back_buffer.Get(),
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
    hr = back_buffer.As(&surface);
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

void D2DBackend::ReleaseTargetBitmap() noexcept
{
    if (d2d_context_)
        d2d_context_->SetTarget(nullptr);
    if (d3d_context_)
        d3d_context_->OMSetRenderTargets(0, nullptr, nullptr);
    target_bitmap_.Reset();
    depth_stencil_view_.Reset();
    depth_texture_.Reset();
    render_target_view_.Reset();
}

bool D2DBackend::Resize(std::uint32_t width, std::uint32_t height) noexcept
{
    if (!swap_chain_ || !d2d_context_)
        return false;

    width_ = std::max<std::uint32_t>(1u, width);
    height_ = std::max<std::uint32_t>(1u, height);

    ReleaseTargetBitmap();

    HRESULT hr = swap_chain_->ResizeBuffers(
        0,
        width_,
        height_,
        DXGI_FORMAT_UNKNOWN,
        0);
    if (FAILED(hr))
        return false;

    return CreateTargetBitmap();
}

bool D2DBackend::SyncSceneGeometry(const LevelScene* scene, std::uint64_t scene_version) noexcept
{
    if (cached_scene_version_ == scene_version)
        return true;

    floor_geometries_.clear();
    if (scene == nullptr)
    {
        cached_scene_version_ = scene_version;
        return true;
    }

    floor_geometries_.reserve(scene->geometries.size());
    for (const EeGeometry& geometry : scene->geometries)
    {
        ComPtr<ID2D1PathGeometry> path;
        HRESULT hr = d2d_factory_->CreatePathGeometry(path.GetAddressOf());
        if (FAILED(hr))
            return false;

        ComPtr<ID2D1GeometrySink> sink;
        hr = path->Open(sink.GetAddressOf());
        if (FAILED(hr))
            return false;

        const EePoint& first = scene->points[geometry.point_offset];
        sink->BeginFigure(D2D1::Point2F(first.x, first.y), D2D1_FIGURE_BEGIN_FILLED);
        for (std::uint32_t i = 1; i < geometry.point_count; ++i)
        {
            const EePoint& point = scene->points[geometry.point_offset + i];
            sink->AddLine(D2D1::Point2F(point.x, point.y));
        }
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);
        hr = sink->Close();
        if (FAILED(hr))
            return false;

        floor_geometries_.push_back(std::move(path));
    }

    cached_scene_version_ = scene_version;
    return true;
}

void D2DBackend::SyncIconAssets(std::uint64_t icon_assets_version) noexcept
{
    if (cached_icon_assets_version_ == icon_assets_version)
        return;

    icon_bitmaps_.clear();
    cached_icon_assets_version_ = icon_assets_version;
}

ComPtr<ID2D1Bitmap1> D2DBackend::LoadBitmap(const std::wstring& path) noexcept
{
    ComPtr<ID2D1Bitmap1> bitmap;
    if (path.empty() || !wic_factory_ || !d2d_context_)
        return bitmap;

    ComPtr<IWICBitmapDecoder> decoder;
    HRESULT hr = wic_factory_->CreateDecoderFromFilename(
        path.c_str(),
        nullptr,
        GENERIC_READ,
        WICDecodeMetadataCacheOnLoad,
        decoder.GetAddressOf());
    if (FAILED(hr))
        return bitmap;

    ComPtr<IWICBitmapFrameDecode> frame;
    hr = decoder->GetFrame(0, frame.GetAddressOf());
    if (FAILED(hr))
        return bitmap;

    ComPtr<IWICFormatConverter> converter;
    hr = wic_factory_->CreateFormatConverter(converter.GetAddressOf());
    if (FAILED(hr))
        return bitmap;

    hr = converter->Initialize(
        frame.Get(),
        GUID_WICPixelFormat32bppPBGRA,
        WICBitmapDitherTypeNone,
        nullptr,
        0.0,
        WICBitmapPaletteTypeCustom);
    if (FAILED(hr))
        return bitmap;

    hr = d2d_context_->CreateBitmapFromWicBitmap(
        converter.Get(),
        nullptr,
        bitmap.GetAddressOf());
    if (FAILED(hr))
        bitmap.Reset();

    return bitmap;
}

D2DBackend::IconBitmapSet* D2DBackend::GetIconBitmaps(
    std::uint32_t icon_id,
    const IconAssetTable* icon_assets) noexcept
{
    if (icon_assets == nullptr)
        return nullptr;

    IconBitmapSet& cached = icon_bitmaps_[icon_id];
    if (cached.attempted)
        return &cached;

    cached.attempted = true;
    const auto found = icon_assets->find(icon_id);
    if (found == icon_assets->end())
        return &cached;

    cached.image = LoadBitmap(found->second.image_path);
    if (!found->second.outline_path.empty())
        cached.outline = LoadBitmap(found->second.outline_path);
    return &cached;
}

void D2DBackend::DrawBitmapCentered(
    ID2D1Bitmap1* bitmap,
    float center_x,
    float center_y,
    float requested_size,
    float angle_radians,
    bool flipped) noexcept
{
    if (bitmap == nullptr)
        return;

    const D2D1_SIZE_U pixels = bitmap->GetPixelSize();
    const std::uint32_t max_dimension = std::max(pixels.width, pixels.height);
    if (max_dimension == 0)
        return;

    const float size = std::clamp(requested_size, 10.0f, 96.0f);
    const float scale = size / static_cast<float>(max_dimension);
    const float draw_width = static_cast<float>(pixels.width) * scale;
    const float draw_height = static_cast<float>(pixels.height) * scale;
    const float cosine = std::cos(angle_radians);
    const float sine = std::sin(angle_radians);
    const float flip_x = flipped ? -1.0f : 1.0f;

    d2d_context_->SetTransform(D2D1::Matrix3x2F(
        flip_x * cosine,
        flip_x * sine,
        -sine,
        cosine,
        center_x,
        center_y));

    d2d_context_->DrawBitmap(
        bitmap,
        D2D1::RectF(
            -draw_width * 0.5f,
            -draw_height * 0.5f,
            draw_width * 0.5f,
            draw_height * 0.5f),
        1.0f,
        D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
}

void D2DBackend::QueryVisibleFloors(
    const LevelScene& scene,
    float camera_x,
    float camera_y,
    float zoom,
    RenderFrameStats& stats) noexcept
{
    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float half_width = static_cast<float>(width_) * 0.5f / zoom;
    const float half_height = static_cast<float>(height_) * 0.5f / zoom;
    constexpr float margin = 2.5f;

    const auto started = std::chrono::steady_clock::now();
    scene.Query(
        camera_x - half_width - margin,
        camera_y - half_height - margin,
        camera_x + half_width + margin,
        camera_y + half_height + margin,
        visible_candidates_);
    std::sort(visible_candidates_.begin(), visible_candidates_.end(), std::greater<>());
    const auto finished = std::chrono::steady_clock::now();

    stats.cull_ms = std::chrono::duration<double, std::milli>(finished - started).count();
    stats.visible_candidates = static_cast<std::uint32_t>(
        std::min<std::size_t>(visible_candidates_.size(), UINT32_MAX));
}

void D2DBackend::DrawSceneOverlays(
    const LevelScene& scene,
    const IconAssetTable* icon_assets,
    float camera_x,
    float camera_y,
    float zoom,
    std::int32_t selected_floor,
    RenderFrameStats& stats) noexcept
{
    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float selection_world = 2.0f / zoom;
    const float screen_center_x = static_cast<float>(width_) * 0.5f;
    const float screen_center_y = static_cast<float>(height_) * 0.5f;

    if (selected_floor >= 0)
    {
        const std::uint32_t selected = static_cast<std::uint32_t>(selected_floor);
        if (std::find(visible_candidates_.begin(), visible_candidates_.end(), selected) != visible_candidates_.end() &&
            selected < scene.floors.size())
        {
            const EeFloor& floor = scene.floors[selected];
            if (floor.geometry_id < floor_geometries_.size())
            {
                const float center_x = (floor.x - camera_x) * zoom + screen_center_x;
                const float center_y = (camera_y - floor.y) * zoom + screen_center_y;
                const float cosine = std::cos(floor.entry_angle);
                const float sine = std::sin(floor.entry_angle);
                d2d_context_->SetTransform(D2D1::Matrix3x2F(
                    zoom * cosine,
                    -zoom * sine,
                    -zoom * sine,
                    -zoom * cosine,
                    center_x,
                    center_y));
                d2d_context_->DrawGeometry(
                    floor_geometries_[floor.geometry_id].Get(),
                    selection_brush_.Get(),
                    selection_world);
                ++stats.draw_calls;
            }
        }
    }

    if (zoom >= 12.0f && icon_assets != nullptr)
    {
        for (std::uint32_t floor_index : visible_candidates_)
        {
            if (floor_index >= scene.floors.size())
                continue;

            const EeFloor& floor = scene.floors[floor_index];
            if (floor.icon_id == EE_ICON_NONE)
                continue;

            IconBitmapSet* bitmaps = GetIconBitmaps(floor.icon_id, icon_assets);
            if (bitmaps == nullptr || !bitmaps->image)
                continue;

            const float center_x = (floor.x - camera_x) * zoom + screen_center_x;
            const float center_y = (camera_y - floor.y) * zoom + screen_center_y;
            const bool is_floor_icon = (floor.icon_flags & EE_ICON_FLAG_FLOOR) != 0;
            const bool flipped = (floor.icon_flags & EE_ICON_FLAG_FLIPPED) != 0;
            const float size = zoom * (is_floor_icon ? 0.78f : 0.62f);

            if (is_floor_icon && bitmaps->outline)
            {
                DrawBitmapCentered(
                    bitmaps->outline.Get(),
                    center_x,
                    center_y,
                    size * 1.04f,
                    floor.icon_angle,
                    flipped);
                ++stats.draw_calls;
            }

            DrawBitmapCentered(
                bitmaps->image.Get(),
                center_x,
                center_y,
                size,
                floor.icon_angle,
                flipped);
            ++stats.icon_draws;
            ++stats.draw_calls;
        }
    }

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
}

void D2DBackend::DrawPlaybackPlanets(
    const PlaybackVisualState& playback,
    float camera_x,
    float camera_y,
    float zoom) noexcept
{
    if (!playback.active)
        return;

    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float center_x = static_cast<float>(width_) * 0.5f;
    const float center_y = static_cast<float>(height_) * 0.5f;
    const float radius = std::clamp(zoom * 0.28f, 6.0f, 28.0f);

    const D2D1_POINT_2F stationary = D2D1::Point2F(
        (playback.stationary_x - camera_x) * zoom + center_x,
        (camera_y - playback.stationary_y) * zoom + center_y);
    const D2D1_POINT_2F orbiting = D2D1::Point2F(
        (playback.orbiting_x - camera_x) * zoom + center_x,
        (camera_y - playback.orbiting_y) * zoom + center_y);

    ID2D1SolidColorBrush* stationary_brush = playback.stationary_is_red
        ? planet_red_brush_.Get()
        : planet_blue_brush_.Get();
    ID2D1SolidColorBrush* orbiting_brush = playback.stationary_is_red
        ? planet_blue_brush_.Get()
        : planet_red_brush_.Get();

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
    const D2D1_ELLIPSE stationary_ellipse = D2D1::Ellipse(stationary, radius, radius);
    const D2D1_ELLIPSE orbiting_ellipse = D2D1::Ellipse(orbiting, radius, radius);
    d2d_context_->FillEllipse(stationary_ellipse, stationary_brush);
    d2d_context_->DrawEllipse(stationary_ellipse, planet_outline_brush_.Get(), 1.5f);
    d2d_context_->FillEllipse(orbiting_ellipse, orbiting_brush);
    d2d_context_->DrawEllipse(orbiting_ellipse, planet_outline_brush_.Get(), 1.5f);
}

HRESULT D2DBackend::EndD2DDraw() noexcept
{
    HRESULT hr = d2d_context_->EndDraw();
    if (hr != D2DERR_RECREATE_TARGET)
        return hr;

    ReleaseTargetBitmap();
    return CreateTargetBitmap() ? S_FALSE : hr;
}

HRESULT D2DBackend::RenderFrame(
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
    RenderFrameStats& stats) noexcept
{
    stats = {};
    if (!d2d_context_ || !swap_chain_ || !target_bitmap_ ||
        !render_target_view_ || !depth_stencil_view_)
        return E_FAIL;

    if (!SyncSceneGeometry(scene, scene_version))
        return E_FAIL;
    if (scene != nullptr && !floor_renderer_.SyncGeometry(*scene, scene_version))
        return E_FAIL;
    SyncIconAssets(icon_assets_version);
    if (!icon_renderer_.SyncAssets(icon_assets, icon_assets_version))
        return E_FAIL;

    d2d_context_->BeginDraw();
    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
    d2d_context_->Clear(D2D1::ColorF(0x14161A));

    constexpr float grid_step = 64.0f;
    for (float x = 0.0f; x <= static_cast<float>(width_); x += grid_step)
    {
        d2d_context_->DrawLine(
            D2D1::Point2F(x, 0.0f),
            D2D1::Point2F(x, static_cast<float>(height_)),
            grid_brush_.Get(),
            1.0f);
        ++stats.draw_calls;
    }
    for (float y = 0.0f; y <= static_cast<float>(height_); y += grid_step)
    {
        d2d_context_->DrawLine(
            D2D1::Point2F(0.0f, y),
            D2D1::Point2F(static_cast<float>(width_), y),
            grid_brush_.Get(),
            1.0f);
        ++stats.draw_calls;
    }

    HRESULT hr = EndD2DDraw();
    if (hr == S_FALSE)
        return S_OK;
    if (FAILED(hr))
        return hr;

    if (scene != nullptr)
    {
        QueryVisibleFloors(*scene, camera_x, camera_y, zoom, stats);

        InstancedFloorDrawStats floor_stats;
        if (!floor_renderer_.Draw(
                d3d_context_.Get(),
                render_target_view_.Get(),
                depth_stencil_view_.Get(),
                *scene,
                visible_candidates_,
                camera_x,
                camera_y,
                std::clamp(zoom, 0.05f, 400.0f),
                width_,
                height_,
                floor_stats))
            return E_FAIL;

        stats.floor_draws = floor_stats.floor_instances;
        stats.draw_calls += floor_stats.draw_calls;

        InstancedIconDrawStats icon_stats;
        if (!icon_renderer_.Draw(
                d3d_context_.Get(),
                render_target_view_.Get(),
                depth_stencil_view_.Get(),
                *scene,
                visible_candidates_,
                camera_x,
                camera_y,
                std::clamp(zoom, 0.05f, 400.0f),
                width_,
                height_,
                icon_stats))
            return E_FAIL;

        stats.icon_draws = icon_stats.icon_instances;
        stats.draw_calls += icon_stats.draw_calls;
    }

    d2d_context_->BeginDraw();
    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());

    if (scene != nullptr)
    {
        // Icons are already drawn by D3D11 with per-floor depth. Passing null here
        // keeps the legacy Direct2D path available as fallback code without drawing
        // a second, always-on-top copy.
        DrawSceneOverlays(
            *scene,
            nullptr,
            camera_x,
            camera_y,
            zoom,
            selected_floor,
            stats);
        DrawPlaybackPlanets(playback, camera_x, camera_y, zoom);
        if (playback.active)
            stats.draw_calls += 4u;
    }
    else
    {
        const float pulse = 0.5f + 0.5f * static_cast<float>(std::sin(seconds * 2.0));
        const float radius = 28.0f + 10.0f * pulse;
        const D2D1_POINT_2F center = D2D1::Point2F(
            static_cast<float>(width_) * 0.5f,
            static_cast<float>(height_) * 0.5f);
        d2d_context_->FillEllipse(D2D1::Ellipse(center, radius, radius), accent_brush_.Get());
        ++stats.draw_calls;
    }

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
    const D2D1_RECT_F border = D2D1::RectF(
        0.5f,
        0.5f,
        std::max(1.0f, static_cast<float>(width_) - 0.5f),
        std::max(1.0f, static_cast<float>(height_) - 0.5f));
    d2d_context_->DrawRectangle(border, border_brush_.Get(), 1.0f);
    ++stats.draw_calls;

    hr = EndD2DDraw();
    if (hr == S_FALSE)
        return S_OK;
    if (FAILED(hr))
        return hr;

    return swap_chain_->Present(1, 0);
}
}
