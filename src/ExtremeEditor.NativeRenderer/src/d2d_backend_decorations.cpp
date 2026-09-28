#include "d2d_backend.h"
#include "static_decoration_transform.h"

#include <algorithm>
#include <cmath>
#include <d2d1effects.h>

namespace ee
{
namespace
{
thread_local ID2D1DeviceContext* last_decoration_context = nullptr;
}

bool D2DBackend::SyncStaticDecorations(
    const std::vector<EeStaticDecoration>* decorations,
    std::uint64_t decorations_version,
    const DecorationAssetTable* assets,
    std::uint64_t assets_version) noexcept
{
    if (!d2d_context_)
        return false;

    const bool device_changed = last_decoration_context != d2d_context_.Get();
    if (device_changed)
    {
        last_decoration_context = d2d_context_.Get();
        decoration_color_effect_.Reset();
        decoration_bitmaps_.clear();
        cached_decoration_assets_version_ = std::numeric_limits<std::uint64_t>::max();
    }

    if (!decoration_color_effect_)
    {
        HRESULT hr = d2d_context_->CreateEffect(
            CLSID_D2D1ColorMatrix,
            decoration_color_effect_.GetAddressOf());
        if (FAILED(hr))
            return false;
    }

    if (cached_static_decorations_version_ != decorations_version)
    {
        static_decorations_.clear();
        if (decorations != nullptr)
            static_decorations_ = *decorations;
        std::stable_sort(
            static_decorations_.begin(),
            static_decorations_.end(),
            StaticDecorationDrawOrderLess);
        cached_static_decorations_version_ = decorations_version;
    }

    if (cached_decoration_assets_version_ != assets_version)
    {
        decoration_bitmaps_.clear();
        if (assets != nullptr)
        {
            for (const auto& entry : *assets)
            {
                const DecorationAsset& asset = entry.second;
                if (asset.width == 0u || asset.height == 0u || asset.stride == 0u ||
                    asset.pixels.empty())
                {
                    continue;
                }

                Microsoft::WRL::ComPtr<ID2D1Bitmap1> bitmap;
                const D2D1_BITMAP_PROPERTIES1 properties = D2D1::BitmapProperties1(
                    D2D1_BITMAP_OPTIONS_NONE,
                    D2D1::PixelFormat(
                        DXGI_FORMAT_B8G8R8A8_UNORM,
                        D2D1_ALPHA_MODE_PREMULTIPLIED));
                HRESULT hr = d2d_context_->CreateBitmap(
                    D2D1::SizeU(asset.width, asset.height),
                    asset.pixels.data(),
                    asset.stride,
                    &properties,
                    bitmap.GetAddressOf());
                if (FAILED(hr))
                    bitmap.Reset();
                if (bitmap)
                    decoration_bitmaps_.emplace(entry.first, std::move(bitmap));
            }
        }
        cached_decoration_assets_version_ = assets_version;
    }

    return true;
}

void D2DBackend::DrawStaticDecorationsCamera(
    const LevelScene& scene,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    RenderFrameStats& stats) noexcept
{
    if (!d2d_context_ || !decoration_color_effect_ || static_decorations_.empty())
        return;

    zoom = std::clamp(zoom, 0.05f, 400.0f);
    constexpr float pixels_per_unit = 100.0f;

    for (const EeStaticDecoration& decoration : static_decorations_)
    {
        if ((decoration.flags & EE_DECORATION_VISIBLE) == 0u ||
            !std::isfinite(decoration.opacity) || decoration.opacity <= 0.0f)
            continue;

        const auto bitmap_it = decoration_bitmaps_.find(decoration.asset_id);
        if (bitmap_it == decoration_bitmaps_.end() || !bitmap_it->second)
            continue;

        float anchor_x = 0.0f;
        float anchor_y = 0.0f;
        if (decoration.relative_mode == EE_DECORATION_RELATIVE_TILE)
        {
            if (decoration.floor < 0 ||
                static_cast<std::size_t>(decoration.floor) >= scene.floors.size())
                continue;
            const EeFloor& floor = scene.floors[static_cast<std::size_t>(decoration.floor)];
            anchor_x = floor.x;
            anchor_y = floor.y;
        }
        else if (decoration.relative_mode != EE_DECORATION_RELATIVE_GLOBAL)
        {
            continue;
        }

        StaticDecorationScreenTransform transform =
            CalculateStaticDecorationScreenTransform(
            decoration,
            anchor_x,
            anchor_y,
            camera_x,
            camera_y,
            zoom,
            camera_rotation,
            width_,
            height_,
            pixels_per_unit);

        ID2D1Bitmap1* bitmap = bitmap_it->second.Get();
        const D2D1_SIZE_U pixels = bitmap->GetPixelSize();
        if (pixels.width == 0u || pixels.height == 0u)
            continue;

        const float sx = transform.m11;
        const float sy = transform.m22;
        if (!std::isfinite(sx) || !std::isfinite(sy) ||
            (!std::isfinite(transform.m12)) || (!std::isfinite(transform.m21)) ||
            std::abs(decoration.scale_x) <= 0.000001f ||
            std::abs(decoration.scale_y) <= 0.000001f)
            continue;

        d2d_context_->SetTransform(D2D1::Matrix3x2F(
            transform.m11,
            transform.m12,
            transform.m21,
            transform.m22,
            transform.center_x,
            transform.center_y));

        const float red = static_cast<float>((decoration.color >> 16) & 0xffu) / 255.0f;
        const float green = static_cast<float>((decoration.color >> 8) & 0xffu) / 255.0f;
        const float blue = static_cast<float>(decoration.color & 0xffu) / 255.0f;
        const float alpha = std::clamp(decoration.opacity, 0.0f, 1.0f);
        const D2D1_MATRIX_5X4_F matrix =
        {
            red, 0.0f, 0.0f, 0.0f,
            0.0f, green, 0.0f, 0.0f,
            0.0f, 0.0f, blue, 0.0f,
            0.0f, 0.0f, 0.0f, alpha,
            0.0f, 0.0f, 0.0f, 0.0f
        };
        decoration_color_effect_->SetInput(0, bitmap);
        decoration_color_effect_->SetValue(D2D1_COLORMATRIX_PROP_COLOR_MATRIX, matrix);
        decoration_color_effect_->SetValue(
            D2D1_COLORMATRIX_PROP_ALPHA_MODE,
            D2D1_COLORMATRIX_ALPHA_MODE_PREMULTIPLIED);

        const D2D1_POINT_2F offset = D2D1::Point2F(
            -static_cast<float>(pixels.width) * 0.5f,
            -static_cast<float>(pixels.height) * 0.5f);
        const D2D1_RECT_F source = D2D1::RectF(
            0.0f,
            0.0f,
            static_cast<float>(pixels.width),
            static_cast<float>(pixels.height));
        d2d_context_->DrawImage(
            decoration_color_effect_.Get(),
            &offset,
            &source,
            D2D1_INTERPOLATION_MODE_LINEAR,
            D2D1_COMPOSITE_MODE_SOURCE_OVER);
        ++stats.draw_calls;
    }

    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
}
}
