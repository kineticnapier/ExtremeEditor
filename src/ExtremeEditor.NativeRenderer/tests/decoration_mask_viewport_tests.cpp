#include "decoration_mask_effects.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <iostream>
#include <vector>

#include <d2d1_1.h>
#include <d2d1effects.h>
#include <d2d1effects_2.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>

namespace
{
using Microsoft::WRL::ComPtr;

constexpr std::uint32_t ViewportWidth = 800u;
constexpr std::uint32_t ViewportHeight = 600u;
constexpr std::uint32_t MaskSize = 100u;

struct Pixel
{
    std::uint8_t blue;
    std::uint8_t green;
    std::uint8_t red;
    std::uint8_t alpha;
};

struct TestDevice
{
    ComPtr<ID3D11Device> d3d_device;
    ComPtr<ID2D1Factory1> factory;
    ComPtr<ID2D1Device> d2d_device;
    ComPtr<ID2D1DeviceContext> context;
};

bool CreateTestDevice(TestDevice& result)
{
    D3D_FEATURE_LEVEL feature_level{};
    if (FAILED(D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,
            0u,
            D3D11_SDK_VERSION,
            result.d3d_device.GetAddressOf(),
            &feature_level,
            nullptr)))
    {
        return false;
    }

    ComPtr<IDXGIDevice> dxgi_device;
    if (FAILED(result.d3d_device.As(&dxgi_device)) ||
        FAILED(D2D1CreateFactory(
            D2D1_FACTORY_TYPE_SINGLE_THREADED,
            __uuidof(ID2D1Factory1),
            nullptr,
            reinterpret_cast<void**>(result.factory.GetAddressOf()))) ||
        FAILED(result.factory->CreateDevice(dxgi_device.Get(), result.d2d_device.GetAddressOf())) ||
        FAILED(result.d2d_device->CreateDeviceContext(
            D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
            result.context.GetAddressOf())))
    {
        return false;
    }
    return true;
}

ComPtr<ID2D1Bitmap1> CreateSolidBitmap(
    ID2D1DeviceContext* context,
    std::uint32_t width,
    std::uint32_t height,
    Pixel color)
{
    std::vector<Pixel> pixels(static_cast<std::size_t>(width) * height, color);
    ComPtr<ID2D1Bitmap1> bitmap;
    const D2D1_BITMAP_PROPERTIES1 properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_NONE,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
    if (FAILED(context->CreateBitmap(
            D2D1::SizeU(width, height),
            pixels.data(),
            width * sizeof(Pixel),
            &properties,
            bitmap.GetAddressOf())))
    {
        return {};
    }
    return bitmap;
}

ComPtr<ID2D1Effect> CreateCenteredMask(
    ID2D1DeviceContext* context,
    ID2D1Bitmap1* mask_bitmap)
{
    ComPtr<ID2D1Effect> transform;
    if (FAILED(context->CreateEffect(CLSID_D2D12DAffineTransform, transform.GetAddressOf())))
        return {};
    transform->SetInput(0, mask_bitmap);
    const float x = (static_cast<float>(ViewportWidth) - MaskSize) * 0.5f;
    const float y = (static_cast<float>(ViewportHeight) - MaskSize) * 0.5f;
    if (FAILED(transform->SetValue(
            D2D1_2DAFFINETRANSFORM_PROP_TRANSFORM_MATRIX,
            D2D1::Matrix3x2F::Translation(x, y))))
    {
        return {};
    }
    return transform;
}

bool ReadPixels(
    ID2D1DeviceContext* context,
    ID2D1Image* image,
    std::vector<Pixel>& pixels)
{
    const D2D1_BITMAP_PROPERTIES1 target_properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
    ComPtr<ID2D1Bitmap1> target;
    if (FAILED(context->CreateBitmap(
            D2D1::SizeU(ViewportWidth, ViewportHeight),
            nullptr,
            0u,
            &target_properties,
            target.GetAddressOf())))
    {
        return false;
    }

    context->SetTarget(target.Get());
    context->BeginDraw();
    context->SetTransform(D2D1::Matrix3x2F::Identity());
    context->Clear(D2D1::ColorF(0.0f, 0.0f));
    const D2D1_RECT_F viewport = D2D1::RectF(
        0.0f,
        0.0f,
        static_cast<float>(ViewportWidth),
        static_cast<float>(ViewportHeight));
    context->DrawImage(
        image,
        nullptr,
        &viewport,
        D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
        D2D1_COMPOSITE_MODE_SOURCE_COPY);
    if (FAILED(context->EndDraw()))
        return false;
    context->SetTarget(nullptr);

    const D2D1_BITMAP_PROPERTIES1 read_properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target->GetPixelFormat());
    ComPtr<ID2D1Bitmap1> readable;
    if (FAILED(context->CreateBitmap(
            D2D1::SizeU(ViewportWidth, ViewportHeight),
            nullptr,
            0u,
            &read_properties,
            readable.GetAddressOf())) ||
        FAILED(readable->CopyFromBitmap(nullptr, target.Get(), nullptr)))
    {
        return false;
    }

    D2D1_MAPPED_RECT mapped{};
    if (FAILED(readable->Map(D2D1_MAP_OPTIONS_READ, &mapped)))
        return false;
    pixels.resize(static_cast<std::size_t>(ViewportWidth) * ViewportHeight);
    for (std::uint32_t y = 0u; y < ViewportHeight; ++y)
    {
        const auto* row = reinterpret_cast<const Pixel*>(mapped.bits + mapped.pitch * y);
        std::copy_n(row, ViewportWidth, pixels.data() + static_cast<std::size_t>(y) * ViewportWidth);
    }
    readable->Unmap();
    return true;
}

const Pixel& At(const std::vector<Pixel>& pixels, std::uint32_t x, std::uint32_t y)
{
    return pixels[static_cast<std::size_t>(y) * ViewportWidth + x];
}

bool IsVisible(const Pixel& pixel)
{
    return pixel.alpha >= 200u;
}

bool DiffersFromBackground(const Pixel& pixel)
{
    constexpr int background = 64;
    return std::abs(static_cast<int>(pixel.blue) - background) > 16 ||
           std::abs(static_cast<int>(pixel.green) - background) > 16 ||
           std::abs(static_cast<int>(pixel.red) - background) > 16;
}

bool VerifyCoverage(
    ID2D1DeviceContext* context,
    ID2D1Effect* mask,
    bool visible_outside,
    bool custom_blend)
{
    const ComPtr<ID2D1Bitmap1> foreground = CreateSolidBitmap(
        context,
        ViewportWidth,
        ViewportHeight,
        Pixel{0u, 0u, 255u, 255u});
    const ComPtr<ID2D1Effect> coverage = ee::CreateDecorationMaskCoverage(
        context,
        mask,
        ViewportWidth,
        ViewportHeight,
        visible_outside);
    ComPtr<ID2D1Effect> alpha_mask;
    if (!foreground || !coverage ||
        FAILED(context->CreateEffect(CLSID_D2D1AlphaMask, alpha_mask.GetAddressOf())))
    {
        return false;
    }
    alpha_mask->SetInput(0, foreground.Get());
    alpha_mask->SetInputEffect(1, coverage.Get());

    ComPtr<ID2D1Image> output;
    if (custom_blend)
    {
        const ComPtr<ID2D1Bitmap1> background = CreateSolidBitmap(
            context,
            ViewportWidth,
            ViewportHeight,
            Pixel{64u, 64u, 64u, 255u});
        ComPtr<ID2D1Effect> blend;
        if (!background || FAILED(context->CreateEffect(CLSID_D2D1Blend, blend.GetAddressOf())))
            return false;
        blend->SetInput(0, background.Get());
        blend->SetInputEffect(1, alpha_mask.Get());
        if (FAILED(blend->SetValue(D2D1_BLEND_PROP_MODE, D2D1_BLEND_MODE_DIFFERENCE)))
            return false;
        blend->GetOutput(output.GetAddressOf());
    }
    else
    {
        alpha_mask->GetOutput(output.GetAddressOf());
    }

    std::vector<Pixel> pixels;
    if (!output || !ReadPixels(context, output.Get(), pixels))
        return false;

    const bool center_affected = custom_blend
        ? DiffersFromBackground(At(pixels, 400u, 300u))
        : IsVisible(At(pixels, 400u, 300u));
    constexpr std::array<std::array<std::uint32_t, 2>, 4> corners =
    {{{10u, 10u}, {789u, 10u}, {10u, 589u}, {789u, 589u}}};
    for (const auto& corner : corners)
    {
        const bool corner_affected = custom_blend
            ? DiffersFromBackground(At(pixels, corner[0], corner[1]))
            : IsVisible(At(pixels, corner[0], corner[1]));
        if (corner_affected != visible_outside)
            return false;
    }
    return center_affected != visible_outside;
}
}

int main()
{
    const HRESULT coinit = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(coinit))
    {
        std::cerr << "FAIL: COM initialization failed.\n";
        return 1;
    }

    TestDevice device;
    if (!CreateTestDevice(device))
    {
        CoUninitialize();
        std::cerr << "FAIL: D2D test device initialization failed.\n";
        return 1;
    }

    const ComPtr<ID2D1Bitmap1> mask_bitmap = CreateSolidBitmap(
        device.context.Get(),
        MaskSize,
        MaskSize,
        Pixel{255u, 255u, 255u, 255u});
    const ComPtr<ID2D1Effect> mask = CreateCenteredMask(device.context.Get(), mask_bitmap.Get());
    if (!mask)
    {
        CoUninitialize();
        std::cerr << "FAIL: mask fixture creation failed.\n";
        return 1;
    }

    bool passed = true;
    if (!VerifyCoverage(device.context.Get(), mask.Get(), false, false))
    {
        std::cerr << "FAIL: VisibleInsideMask normal blend did not match viewport semantics.\n";
        passed = false;
    }
    if (!VerifyCoverage(device.context.Get(), mask.Get(), true, false))
    {
        std::cerr << "FAIL: VisibleOutsideMask normal blend did not cover the viewport outside the mask.\n";
        passed = false;
    }
    if (!VerifyCoverage(device.context.Get(), mask.Get(), false, true))
    {
        std::cerr << "FAIL: VisibleInsideMask custom blend did not match viewport semantics.\n";
        passed = false;
    }
    if (!VerifyCoverage(device.context.Get(), mask.Get(), true, true))
    {
        std::cerr << "FAIL: VisibleOutsideMask custom blend did not cover the viewport outside the mask.\n";
        passed = false;
    }

    device.context.Reset();
    device.d2d_device.Reset();
    device.factory.Reset();
    device.d3d_device.Reset();
    CoUninitialize();
    if (!passed)
        return 1;

    std::cout << "PASS: decoration mask viewport regression is valid.\n";
    return 0;
}
