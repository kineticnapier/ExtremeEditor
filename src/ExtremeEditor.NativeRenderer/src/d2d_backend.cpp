#include "d2d_backend.h"

#include <algorithm>
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
    icon_bitmaps_.clear();
    floor_geometries_.clear();
    visible_candidates_.clear();
    cached_scene_version_ = std::numeric_limits<std::uint64_t>::max();
    cached_icon_assets_version_ = std::numeric_limits<std::uint64_t>::max();
    ReleaseTargetBitmap();
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
    return SUCCEEDED(hr);
}

bool D2DBackend::CreateTargetBitmap() noexcept
{
    ComPtr<IDXGISurface> surface;
    HRESULT hr = swap_chain_->GetBuffer(0, IID_PPV_ARGS(surface.GetAddressOf()));
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
    target_bitmap_.Reset();
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

void D2DBackend::DrawScene(
    const LevelScene& scene,
    const IconAssetTable* icon_assets,
    float camera_x,
    float camera_y,
    float zoom,
    std::int32_t selected_floor) noexcept
{
    zoom = std::clamp(zoom, 0.05f, 400.0f);
    const float half_width = static_cast<float>(width_) * 0.5f / zoom;
    const float half_height = static_cast<float>(height_) * 0.5f / zoom;
    const float margin = 2.5f;

    scene.Query(
        camera_x - half_width - margin,
        camera_y - half_height - margin,
        camera_x + half_width + margin,
        camera_y + half_height + margin,
        visible_candidates_);

    if (visible_candidates_.empty())
        return;

    std::sort(visible_candidates_.begin(), visible_candidates_.end(), std::greater<>());

    constexpr std::size_t MaxIndividualDraw = 80000;
    const std::size_t stride = std::max<std::size_t>(
        1,
        (visible_candidates_.size() + MaxIndividualDraw - 1) / MaxIndividualDraw);

    const float edge_pixels = std::clamp(zoom * 0.022f, 1.0f, 5.0f);
    const float edge_world = edge_pixels / zoom;
    const float selection_world = 2.0f / zoom;
    const float screen_center_x = static_cast<float>(width_) * 0.5f;
    const float screen_center_y = static_cast<float>(height_) * 0.5f;

    for (std::size_t candidate_index = 0; candidate_index < visible_candidates_.size(); candidate_index += stride)
    {
        const std::uint32_t floor_index = visible_candidates_[candidate_index];
        const EeFloor& floor = scene.floors[floor_index];
        if (floor.geometry_id >= floor_geometries_.size())
            continue;

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

        ID2D1PathGeometry* geometry = floor_geometries_[floor.geometry_id].Get();
        d2d_context_->FillGeometry(geometry, floor_brush_.Get());
        d2d_context_->DrawGeometry(geometry, floor_edge_brush_.Get(), edge_world);
        if (selected_floor >= 0 && floor_index == static_cast<std::uint32_t>(selected_floor))
            d2d_context_->DrawGeometry(geometry, selection_brush_.Get(), selection_world);
    }

    if (zoom >= 12.0f && icon_assets != nullptr)
    {
        for (std::size_t candidate_index = 0; candidate_index < visible_candidates_.size(); candidate_index += stride)
        {
            const EeFloor& floor = scene.floors[visible_candidates_[candidate_index]];
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
            }

            DrawBitmapCentered(
                bitmaps->image.Get(),
                center_x,
                center_y,
                size,
                floor.icon_angle,
                flipped);
        }
    }

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
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
    std::int32_t selected_floor) noexcept
{
    if (!d2d_context_ || !swap_chain_ || !target_bitmap_)
        return E_FAIL;

    if (!SyncSceneGeometry(scene, scene_version))
        return E_FAIL;
    SyncIconAssets(icon_assets_version);

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
    }
    for (float y = 0.0f; y <= static_cast<float>(height_); y += grid_step)
    {
        d2d_context_->DrawLine(
            D2D1::Point2F(0.0f, y),
            D2D1::Point2F(static_cast<float>(width_), y),
            grid_brush_.Get(),
            1.0f);
    }

    if (scene != nullptr)
    {
        DrawScene(*scene, icon_assets, camera_x, camera_y, zoom, selected_floor);
    }
    else
    {
        const float pulse = 0.5f + 0.5f * static_cast<float>(std::sin(seconds * 2.0));
        const float radius = 28.0f + 10.0f * pulse;
        const D2D1_POINT_2F center = D2D1::Point2F(
            static_cast<float>(width_) * 0.5f,
            static_cast<float>(height_) * 0.5f);
        d2d_context_->FillEllipse(D2D1::Ellipse(center, radius, radius), accent_brush_.Get());
    }

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
    const D2D1_RECT_F border = D2D1::RectF(
        0.5f,
        0.5f,
        std::max(1.0f, static_cast<float>(width_) - 0.5f),
        std::max(1.0f, static_cast<float>(height_) - 0.5f));
    d2d_context_->DrawRectangle(border, border_brush_.Get(), 1.0f);

    HRESULT hr = d2d_context_->EndDraw();
    if (hr == D2DERR_RECREATE_TARGET)
    {
        ReleaseTargetBitmap();
        return CreateTargetBitmap() ? S_OK : hr;
    }
    if (FAILED(hr))
        return hr;

    return swap_chain_->Present(1, 0);
}
}
