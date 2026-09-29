#include "d2d_backend.h"
#include "diagnostic_flags.h"
#include "static_decoration_transform.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <d2d1effects.h>
#include <string>

namespace ee
{
namespace
{
thread_local ID2D1DeviceContext* last_decoration_context = nullptr;

struct DecorationDrawRejections
{
    std::size_t invalid_asset_id = 0u;
    std::size_t invalid_floor = 0u;
    std::size_t invalid_opacity = 0u;
    std::size_t invalid_scale = 0u;
    std::size_t invalid_position = 0u;
    std::size_t invalid_dimensions = 0u;
    std::size_t unsupported_flags = 0u;
    std::size_t unsupported_placement = 0u;
    std::size_t invalid_struct_data = 0u;
    std::size_t other = 0u;
};

const char* PlacementName(std::uint32_t placement) noexcept
{
    switch (placement)
    {
    case EE_DECORATION_RELATIVE_GLOBAL: return "Global";
    case EE_DECORATION_RELATIVE_TILE: return "Tile";
    case EE_DECORATION_RELATIVE_CAMERA: return "Camera";
    case EE_DECORATION_RELATIVE_CAMERA_ASPECT: return "CameraAspect";
    case EE_DECORATION_RELATIVE_RED_PLANET: return "RedPlanet";
    case EE_DECORATION_RELATIVE_BLUE_PLANET: return "BluePlanet";
    case EE_DECORATION_RELATIVE_GREEN_PLANET: return "GreenPlanet";
    default: return "Other";
    }
}

std::string Utf8AssetName(const std::wstring& path)
{
    if (path.empty())
        return {};
    const std::size_t separator = path.find_last_of(L"\\/");
    const std::wstring name = separator == std::wstring::npos ? path : path.substr(separator + 1u);
    const int bytes = WideCharToMultiByte(
        CP_UTF8, 0, name.c_str(), static_cast<int>(name.size()), nullptr, 0, nullptr, nullptr);
    if (bytes <= 0)
        return {};
    std::string result(static_cast<std::size_t>(bytes), '\0');
    WideCharToMultiByte(
        CP_UTF8, 0, name.c_str(), static_cast<int>(name.size()), result.data(), bytes, nullptr, nullptr);
    return result;
}
}

bool D2DBackend::SyncStaticDecorations(
    const std::vector<EeStaticDecoration>* decorations,
    std::uint64_t decorations_version,
    const DecorationAssetTable* assets,
    std::uint64_t assets_version,
    std::uint64_t scene_generation) noexcept
{
    if (!d2d_context_)
        return false;

    const std::size_t input_count = decorations != nullptr ? decorations->size() : 0u;
    const bool log_sync = DecorationDiagnosticsEnabled() &&
                          (logged_decoration_input_ != decorations ||
                           logged_decoration_input_count_ != input_count ||
                           logged_decoration_input_version_ != decorations_version);
    if (log_sync)
    {
        std::fprintf(
            stderr,
            "[decoration-diagnostic] backendReceivedCount=%zu incomingVersion=%llu "
            "cachedVersion=%llu sceneGeneration=%llu cachedSceneGeneration=%llu\n",
            input_count,
            static_cast<unsigned long long>(decorations_version),
            static_cast<unsigned long long>(cached_static_decorations_version_),
            static_cast<unsigned long long>(scene_generation),
            static_cast<unsigned long long>(cached_static_decorations_scene_generation_));
    }

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

    const bool decoration_scene_changed =
        cached_static_decorations_scene_generation_ != scene_generation;
    const bool copied_decorations = ShouldRefreshSceneLocalCache(
        cached_static_decorations_scene_generation_,
        cached_static_decorations_version_,
        scene_generation,
        decorations_version);
    if (copied_decorations)
    {
        static_decorations_.clear();
        if (decorations != nullptr)
            static_decorations_ = *decorations;
        std::stable_sort(
            static_decorations_.begin(),
            static_decorations_.end(),
            StaticDecorationDrawOrderLess);
        cached_static_decorations_version_ = decorations_version;
        cached_static_decorations_scene_generation_ = scene_generation;
        if (decoration_scene_changed)
            logged_large_decorations_.clear();
    }

    if (log_sync)
    {
        std::fprintf(
            stderr,
            "[decoration-diagnostic] backendStoredCount=%zu storedVersion=%llu copied=%u "
            "inputCountMatches=%u\n",
            static_decorations_.size(),
            static_cast<unsigned long long>(cached_static_decorations_version_),
            copied_decorations ? 1u : 0u,
            static_decorations_.size() == input_count ? 1u : 0u);
        logged_decoration_input_ = decorations;
        logged_decoration_input_count_ = input_count;
        logged_decoration_input_version_ = decorations_version;
    }

    if (ShouldRefreshSceneLocalCache(
            cached_decoration_assets_scene_generation_,
            cached_decoration_assets_version_,
            scene_generation,
            assets_version))
    {
        decoration_bitmaps_.clear();
        decoration_asset_paths_.clear();
        if (assets != nullptr)
        {
            for (const auto& entry : *assets)
            {
                const DecorationAsset& asset = entry.second;
                if (asset.width == 0u || asset.height == 0u || asset.stride == 0u ||
                    !asset.pixels || asset.pixels->empty())
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
                    asset.pixels->data(),
                    asset.stride,
                    &properties,
                    bitmap.GetAddressOf());
                if (FAILED(hr))
                    bitmap.Reset();
                if (bitmap)
                {
                    decoration_bitmaps_.emplace(entry.first, std::move(bitmap));
                    decoration_asset_paths_.emplace(entry.first, asset.source_path);
                }
            }
        }
        cached_decoration_assets_version_ = assets_version;
        cached_decoration_assets_scene_generation_ = scene_generation;
    }

    return true;
}

void D2DBackend::DrawStaticDecorationsCamera(
    const LevelScene& scene,
    const PlaybackVisualState& playback,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    RenderFrameStats& stats) noexcept
{
    const auto log_draw_targets = [this](
        std::size_t draw_count,
        const DecorationDrawRejections& rejected) noexcept
    {
        if (!DecorationDiagnosticsEnabled())
            return;
        if (logged_decoration_source_count_ == static_decorations_.size() &&
            logged_decoration_draw_count_ == draw_count &&
            logged_decoration_bitmap_count_ == decoration_bitmaps_.size())
        {
            return;
        }

        std::fprintf(
            stderr,
            "[decoration-diagnostic] nativeStoredCount=%zu nativeDrawTargetCount=%zu nativeBitmapCount=%zu "
            "invalidAssetId=%zu invalidFloor=%zu invalidOpacity=%zu invalidScale=%zu "
            "invalidPosition=%zu invalidDimensions=%zu unsupportedFlags=%zu "
            "unsupportedPlacement=%zu invalidStructData=%zu other=%zu\n",
            static_decorations_.size(),
            draw_count,
            decoration_bitmaps_.size(),
            rejected.invalid_asset_id,
            rejected.invalid_floor,
            rejected.invalid_opacity,
            rejected.invalid_scale,
            rejected.invalid_position,
            rejected.invalid_dimensions,
            rejected.unsupported_flags,
            rejected.unsupported_placement,
            rejected.invalid_struct_data,
            rejected.other);
        logged_decoration_source_count_ = static_decorations_.size();
        logged_decoration_draw_count_ = draw_count;
        logged_decoration_bitmap_count_ = decoration_bitmaps_.size();
    };

    DecorationDrawRejections rejected;
    if (!d2d_context_ || !decoration_color_effect_ || static_decorations_.empty())
    {
        log_draw_targets(0u, rejected);
        return;
    }

    zoom = std::clamp(zoom, 0.05f, 400.0f);
    constexpr float pixels_per_unit = 100.0f;
    std::size_t draw_target_count = 0u;

    for (const EeStaticDecoration& decoration : static_decorations_)
    {
        if ((decoration.flags & EE_DECORATION_VISIBLE) == 0u)
        {
            ++rejected.unsupported_flags;
            continue;
        }
        if (!std::isfinite(decoration.opacity) || decoration.opacity <= 0.0f)
        {
            ++rejected.invalid_opacity;
            continue;
        }

        const auto bitmap_it = decoration_bitmaps_.find(decoration.asset_id);
        if (bitmap_it == decoration_bitmaps_.end() || !bitmap_it->second)
        {
            ++rejected.invalid_asset_id;
            continue;
        }

        float anchor_x = decoration.base_anchor_x;
        float anchor_y = decoration.base_anchor_y;
        float parent_rotation = 0.0f;
        float parent_scale_x = 1.0f;
        float parent_scale_y = 1.0f;
        if (decoration.relative_mode == EE_DECORATION_RELATIVE_TILE)
        {
            if (decoration.floor < 0 ||
                static_cast<std::size_t>(decoration.floor) >= scene.floors.size())
            {
                ++rejected.invalid_floor;
                continue;
            }
            const EeFloor& floor = scene.floors[static_cast<std::size_t>(decoration.floor)];
            if ((decoration.flags & EE_DECORATION_STICK_TO_FLOOR) != 0u)
            {
                anchor_x = floor.x;
                anchor_y = floor.y;
                parent_rotation = floor.transform_rotation;
                if ((floor.track_transform_flags & EE_TRACK_TRANSFORM_ENABLED) != 0u)
                {
                    parent_scale_x = floor.transform_scale_x;
                    parent_scale_y = floor.transform_scale_y;
                }
            }
        }
        else if (decoration.relative_mode == EE_DECORATION_RELATIVE_RED_PLANET ||
                 decoration.relative_mode == EE_DECORATION_RELATIVE_BLUE_PLANET ||
                 decoration.relative_mode == EE_DECORATION_RELATIVE_GREEN_PLANET)
        {
            if (!playback.active)
            {
                ++rejected.unsupported_placement;
                continue;
            }
            const bool wants_red = decoration.relative_mode == EE_DECORATION_RELATIVE_RED_PLANET;
            const bool wants_blue = decoration.relative_mode == EE_DECORATION_RELATIVE_BLUE_PLANET;
            if (wants_red || wants_blue)
            {
                const bool use_stationary = playback.stationary_is_red == wants_red;
                anchor_x = use_stationary ? playback.stationary_x : playback.orbiting_x;
                anchor_y = use_stationary ? playback.stationary_y : playback.orbiting_y;
            }
            else
            {
                // The current playback model exposes two planet poses. Preserve GreenPlanet
                // placement and use their center until the three-planet runtime lands.
                anchor_x = (playback.stationary_x + playback.orbiting_x) * 0.5f;
                anchor_y = (playback.stationary_y + playback.orbiting_y) * 0.5f;
            }
        }
        else if (decoration.relative_mode != EE_DECORATION_RELATIVE_GLOBAL &&
                 decoration.relative_mode != EE_DECORATION_RELATIVE_CAMERA &&
                 decoration.relative_mode != EE_DECORATION_RELATIVE_CAMERA_ASPECT)
        {
            ++rejected.unsupported_placement;
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
            pixels_per_unit,
            parent_rotation,
            parent_scale_x,
            parent_scale_y,
            scene.floors.empty() ? 0.0f : scene.floors.front().x,
            scene.floors.empty() ? 0.0f : scene.floors.front().y);

        ID2D1Bitmap1* bitmap = bitmap_it->second.Get();
        const D2D1_SIZE_U pixels = bitmap->GetPixelSize();
        if (pixels.width == 0u || pixels.height == 0u)
        {
            ++rejected.invalid_dimensions;
            continue;
        }

        const float sx = transform.m11;
        const float sy = transform.m22;
        if (!std::isfinite(sx) || !std::isfinite(sy) ||
            (!std::isfinite(transform.m12)) || (!std::isfinite(transform.m21)))
        {
            ++rejected.invalid_struct_data;
            continue;
        }
        if (std::abs(decoration.scale_x) <= 0.000001f ||
            std::abs(decoration.scale_y) <= 0.000001f)
        {
            ++rejected.invalid_scale;
            continue;
        }

        const StaticDecorationScreenRect screen_rect = CalculateStaticDecorationScreenRect(
            transform,
            static_cast<float>(pixels.width),
            static_cast<float>(pixels.height));
        const float clipped_left = std::max(0.0f, screen_rect.left);
        const float clipped_top = std::max(0.0f, screen_rect.top);
        const float clipped_right = std::min(static_cast<float>(width_), screen_rect.right);
        const float clipped_bottom = std::min(static_cast<float>(height_), screen_rect.bottom);
        const float clipped_width = std::max(0.0f, clipped_right - clipped_left);
        const float clipped_height = std::max(0.0f, clipped_bottom - clipped_top);
        const double viewport_area = static_cast<double>(width_) * static_cast<double>(height_);
        const double coverage = viewport_area > 0.0
            ? static_cast<double>(clipped_width) * clipped_height / viewport_area
            : 0.0;
        if (DecorationDiagnosticsEnabled() && coverage >= 0.5 &&
            logged_large_decorations_.insert(decoration.source_index).second)
        {
            const auto path_it = decoration_asset_paths_.find(decoration.asset_id);
            const std::string asset_name = path_it == decoration_asset_paths_.end()
                ? std::string{}
                : Utf8AssetName(path_it->second);
            std::fprintf(
                stderr,
                "[decoration-large] sourceIndex=%d assetId=%u assetName=%s placement=%s "
                "floor=%d depth=%d opacity=%.6g chartPosition=(%.6g,%.6g) "
                "resolvedWorld=(%.6g,%.6g) screenPosition=(%.6g,%.6g) "
                "pivot=(%.6g,%.6g) rotationRadians=%.6g scale=(%.6g,%.6g) "
                "scaleMultiplier=%.6g bitmap=(%u,%u) finalScreenRect=(%.6g,%.6g,%.6g,%.6g) "
                "viewportCoverage=%.6f stickToFloor=%u lockRotation=%u lockScale=%u "
                "parallax=(%.6g,%.6g) parallaxOffset=(%.6g,%.6g)\n",
                decoration.source_index,
                decoration.asset_id,
                asset_name.empty() ? "<unknown>" : asset_name.c_str(),
                PlacementName(decoration.relative_mode),
                decoration.floor,
                decoration.depth,
                decoration.opacity,
                decoration.chart_position_x,
                decoration.chart_position_y,
                transform.world_x,
                transform.world_y,
                transform.center_x,
                transform.center_y,
                decoration.pivot_offset_x,
                decoration.pivot_offset_y,
                decoration.rotation_radians,
                decoration.scale_x,
                decoration.scale_y,
                decoration.scale_multiplier,
                pixels.width,
                pixels.height,
                screen_rect.left,
                screen_rect.top,
                screen_rect.right,
                screen_rect.bottom,
                coverage,
                (decoration.flags & EE_DECORATION_STICK_TO_FLOOR) != 0u ? 1u : 0u,
                (decoration.flags & EE_DECORATION_LOCK_ROTATION) != 0u ? 1u : 0u,
                (decoration.flags & EE_DECORATION_LOCK_SCALE) != 0u ? 1u : 0u,
                decoration.parallax_x,
                decoration.parallax_y,
                decoration.parallax_offset_x,
                decoration.parallax_offset_y);
        }

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
        ++draw_target_count;
        ++stats.draw_calls;
    }

    log_draw_targets(draw_target_count, rejected);
    d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
}
}
