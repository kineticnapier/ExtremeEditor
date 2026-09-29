#include "level_scene.h"
#include "diagnostic_flags.h"

#include <cstdio>
#include <limits>
#include <memory>
#include <utility>
#include <wincodec.h>
#include <wrl/client.h>

namespace ee
{
namespace
{
DecorationAsset DecodeDecorationAsset(const wchar_t* image_path)
{
    DecorationAsset asset;
    Microsoft::WRL::ComPtr<IWICImagingFactory> factory;
    HRESULT hr = CoCreateInstance(
        CLSID_WICImagingFactory,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(factory.GetAddressOf()));
    if (FAILED(hr))
        return asset;

    Microsoft::WRL::ComPtr<IWICBitmapDecoder> decoder;
    hr = factory->CreateDecoderFromFilename(
        image_path,
        nullptr,
        GENERIC_READ,
        WICDecodeMetadataCacheOnLoad,
        decoder.GetAddressOf());
    if (FAILED(hr))
        return asset;

    Microsoft::WRL::ComPtr<IWICBitmapFrameDecode> frame;
    hr = decoder->GetFrame(0, frame.GetAddressOf());
    if (FAILED(hr))
        return asset;

    Microsoft::WRL::ComPtr<IWICFormatConverter> converter;
    hr = factory->CreateFormatConverter(converter.GetAddressOf());
    if (FAILED(hr))
        return asset;
    hr = converter->Initialize(
        frame.Get(),
        GUID_WICPixelFormat32bppPBGRA,
        WICBitmapDitherTypeNone,
        nullptr,
        0.0,
        WICBitmapPaletteTypeCustom);
    if (FAILED(hr))
        return asset;

    UINT width = 0;
    UINT height = 0;
    hr = converter->GetSize(&width, &height);
    if (FAILED(hr) || width == 0u || height == 0u ||
        width > std::numeric_limits<UINT>::max() / 4u)
    {
        return asset;
    }

    const UINT stride = width * 4u;
    if (height > std::numeric_limits<UINT>::max() / stride)
        return asset;
    const UINT byte_count = stride * height;
    auto pixels = std::make_shared<std::vector<std::uint8_t>>(byte_count);
    hr = converter->CopyPixels(nullptr, stride, byte_count, pixels->data());
    if (FAILED(hr))
        return asset;

    asset.width = width;
    asset.height = height;
    asset.stride = stride;
    asset.pixels = std::move(pixels);
    asset.source_path = image_path;
    return asset;
}
}

bool LevelScene::SetStaticDecorations(
    const EeStaticDecoration* decorations,
    std::uint32_t decoration_count) noexcept
{
    if (decoration_count > 0 && decorations == nullptr)
        return false;

    try
    {
        auto next = std::make_shared<std::vector<EeStaticDecoration>>();
        if (decoration_count > 0)
            next->assign(decorations, decorations + decoration_count);

        std::lock_guard lock(decoration_mutex_);
        const std::size_t previous_count = static_decorations_
            ? static_decorations_->size()
            : 0u;
        if (DecorationDiagnosticsEnabled() &&
            (static_decorations_version_ == 0u || previous_count != decoration_count))
        {
            std::fprintf(
                stderr,
                "[decoration-diagnostic] nativeReceivedCount=%u\n",
                decoration_count);
        }
        static_decorations_ = std::move(next);
        ++static_decorations_version_;
        if (DecorationDiagnosticsEnabled() &&
            (static_decorations_version_ == 1u || previous_count != decoration_count))
        {
            std::fprintf(
                stderr,
                "[decoration-diagnostic] sceneStoredCount=%zu sceneDecorationVersion=%llu\n",
                static_decorations_->size(),
                static_cast<unsigned long long>(static_decorations_version_));
        }
        return true;
    }
    catch (...)
    {
        return false;
    }
}

void LevelScene::ClearDecorationAssets() noexcept
{
    std::lock_guard lock(decoration_mutex_);
    decoration_assets_ = std::make_shared<DecorationAssetTable>();
    ++decoration_assets_version_;
}

bool LevelScene::SetDecorationAsset(
    std::uint32_t asset_id,
    const wchar_t* image_path) noexcept
{
    if (image_path == nullptr || *image_path == L'\0')
        return false;

    try
    {
        DecorationAsset asset = DecodeDecorationAsset(image_path);
        std::lock_guard lock(decoration_mutex_);
        auto next = std::make_shared<DecorationAssetTable>(
            decoration_assets_ ? *decoration_assets_ : DecorationAssetTable{});
        (*next)[asset_id] = std::move(asset);
        decoration_assets_ = std::move(next);
        ++decoration_assets_version_;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

DecorationRenderState LevelScene::GetDecorationRenderState() const noexcept
{
    std::lock_guard lock(decoration_mutex_);
    DecorationRenderState state;
    state.decorations = static_decorations_;
    state.assets = decoration_assets_;
    state.decorations_version = static_decorations_version_;
    state.assets_version = decoration_assets_version_;
    if (DecorationDiagnosticsEnabled() &&
        logged_decoration_handoff_version_ != static_decorations_version_)
    {
        std::fprintf(
            stderr,
            "[decoration-diagnostic] sceneBackendHandoffCount=%zu sceneDecorationVersion=%llu "
            "sceneAssetCount=%zu sceneAssetVersion=%llu sceneGeneration=%llu\n",
            static_decorations_ ? static_decorations_->size() : 0u,
            static_cast<unsigned long long>(static_decorations_version_),
            decoration_assets_ ? decoration_assets_->size() : 0u,
            static_cast<unsigned long long>(decoration_assets_version_),
            static_cast<unsigned long long>(generation));
        logged_decoration_handoff_version_ = static_decorations_version_;
    }
    return state;
}
}
