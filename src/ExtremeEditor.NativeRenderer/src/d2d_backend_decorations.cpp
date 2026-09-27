#include "d2d_backend.h"

#include <algorithm>
#include <cmath>
#include <d2d1effects.h>

namespace ee
{
namespace
{
D2D1_POINT_2F DecorationWorldToScreen(
    float world_x,
    float world_y,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    std::uint32_t width,
    std::uint32_t height) noexcept
{
    const float dx = world_x - camera_x;
    const float dy = world_y - camera_y;
    const float c = std::cos(camera_rotation);
    const float s = std::sin(camera_rotation);
    const float view_x = c * dx + s * dy;
    const float view_y = -s * dx + c * dy;
    return D2D1::Point2F(
        view_x * zoom + static_cast<float>(width) * 0.5f,
        -view_y * zoom + static_cast<float>(height) * 0.5f);
}
}

bool D2DBackend::SyncStaticDecorations(
    const std::vector<EeStaticDecoration>* decorations,
    std::uint64_t decorations_version,
    const DecorationAssetTable* assets,
    std::uint64_t assets_version) noexcept
{
    if (!d2d_context_)
        return false;

    const bool device_changed = decoration_context_ != d2d_context_.Get();
    if (device_changed)
    {
        decoration_context_ = d2d_context_.Get();
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
            [](const EeStaticDecoration& left, const EeStaticDecoration& right)
            {
                if (left.depth != right.depth)
                    return left.depth < right.depth;
                return left.source_index < right.source_index;
            });
        cached_static_decorations_version_ = decorations_version;
    }

    if (cached_decoration_assets_version_ != assets_version)
    {
        decoration_bitmaps_.clear();
        if (assets != nullptr)
        {
            for (const auto& entry : *assets)
            {
                Microsoft::WRL::ComPtr<ID2D1Bitmap1> bitmap = LoadBitmap(entry.second.image_path);
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

        float world_x = decoration.position_x;
        float world_y = decoration.position_y;
        if (decoration.relative_mode == EE_DECORATION_RELATIVE_TILE)
        {
            if (decoration.floor < 0 ||
                static_cast<std::size_t>(decoration.floor) >= scene.floors.size())
                continue;
            const EeFloor& floor = scene.floors[static_cast<std::size_t>(decoration.floor)];
            world_x += floor.x;
            world_y += floor.y;
        }
        else if (decoration.relative_mode != EE_DECORATION_RELATIVE_GLOBAL)
        {
            continue;
        }

        // ADOFAI pivotOffset changes the rotation pivot without changing the
        // unrotated placement. Rotate the offset around that pivot and apply only
        // the resulting displacement.
        const float local_c = std::cos(decoration.rotation_radians);
        const float local_s = std::sin(decoration.rotation_radians);
        const float rotated_pivot_x =
            local_c * decoration.pivot_offset_x - local_s * decoration.pivot_offset_y;
        const float rotated_pivot_y =
            local_s * decoration.pivot_offset_x + local_c * decoration.pivot_offset_y;
        world_x += decoration.pivot_offset_x - rotated_pivot_x;
        world_y += decoration.pivot_offset_y - rotated_pivot_y;

        const D2D1_POINT_2F center = DecorationWorldToScreen(
            world_x,
            world_y,
            camera_x,
            camera_y,
            zoom,
            camera_rotation,
            width_,
            height_);

        ID2D1Bitmap1* bitmap = bitmap_it->second.Get();
        const D2D1_SIZE_U pixels = bitmap->GetPixelSize();
        if (pixels.width == 0u || pixels.height == 0u)
            continue;

        const float sx = zoom * decoration.scale_x / pixels_per_unit;
        const float sy = zoom * decoration.scale_y / pixels_per_unit;
        if (!std::isfinite(sx) || !std::isfinite(sy) ||
            std::abs(sx) <= 0.000001f || std::abs(sy) <= 0.000001f)
            continue;

        const float angle = decoration.rotation_radians + camera_rotation;
        const float c = std::cos(angle);
        const float s = std::sin(angle);
        d2d_context_->SetTransform(D2D1::Matrix3x2F(
            sx * c,
            sx * s,
            -sy * s,
            sy * c,
            center.x,
            center.y));

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
