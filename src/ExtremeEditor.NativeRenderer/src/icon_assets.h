#pragma once

#include "extreme_editor_renderer.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string>
#include <unordered_map>

namespace ee
{
struct SpriteMetadata
{
    bool valid = false;
    float sprite_rect_x = 0.0f;
    float sprite_rect_y = 0.0f;
    float sprite_rect_width = 0.0f;
    float sprite_rect_height = 0.0f;
    float texture_rect_x = 0.0f;
    float texture_rect_y = 0.0f;
    float texture_rect_width = 0.0f;
    float texture_rect_height = 0.0f;
    float pivot_x = 0.0f;
    float pivot_y = 0.0f;
    float pixels_per_unit = 0.0f;

    static SpriteMetadata FromAbi(const EeSpriteMetadata& source) noexcept
    {
        SpriteMetadata result;
        result.valid = (source.flags & EE_SPRITE_METADATA_VALID) != 0u &&
            std::isfinite(source.sprite_rect_width) && source.sprite_rect_width > 0.0f &&
            std::isfinite(source.sprite_rect_height) && source.sprite_rect_height > 0.0f &&
            std::isfinite(source.texture_rect_width) && source.texture_rect_width > 0.0f &&
            std::isfinite(source.texture_rect_height) && source.texture_rect_height > 0.0f &&
            std::isfinite(source.pixels_per_unit) && source.pixels_per_unit > 0.0f;
        if (!result.valid)
            return result;

        result.sprite_rect_x = source.sprite_rect_x;
        result.sprite_rect_y = source.sprite_rect_y;
        result.sprite_rect_width = source.sprite_rect_width;
        result.sprite_rect_height = source.sprite_rect_height;
        result.texture_rect_x = source.texture_rect_x;
        result.texture_rect_y = source.texture_rect_y;
        result.texture_rect_width = source.texture_rect_width;
        result.texture_rect_height = source.texture_rect_height;
        result.pivot_x = source.pivot_x;
        result.pivot_y = source.pivot_y;
        result.pixels_per_unit = source.pixels_per_unit;
        return result;
    }
};

struct SpriteDrawLayout
{
    float width = 0.0f;
    float height = 0.0f;
    float offset_x = 0.0f;
    float offset_y = 0.0f;
};

inline SpriteDrawLayout CalculateSpriteDrawLayout(
    const SpriteMetadata& metadata,
    std::uint32_t bitmap_width,
    std::uint32_t bitmap_height,
    float requested_size) noexcept
{
    SpriteDrawLayout result;
    const float size = std::isfinite(requested_size)
        ? std::max(requested_size, 0.0f)
        : 0.0f;
    if (!metadata.valid)
    {
        const std::uint32_t max_dimension = std::max(bitmap_width, bitmap_height);
        if (max_dimension == 0u)
            return result;
        const float scale = size / static_cast<float>(max_dimension);
        result.width = static_cast<float>(bitmap_width) * scale;
        result.height = static_cast<float>(bitmap_height) * scale;
        return result;
    }

    const float canvas_dimension = std::max(
        metadata.sprite_rect_width,
        metadata.sprite_rect_height);
    const float scale = size / canvas_dimension;
    result.width = metadata.texture_rect_width * scale;
    result.height = metadata.texture_rect_height * scale;

    const float crop_center_x =
        metadata.texture_rect_x - metadata.sprite_rect_x + metadata.texture_rect_width * 0.5f;
    const float crop_center_y =
        metadata.texture_rect_y - metadata.sprite_rect_y + metadata.texture_rect_height * 0.5f;
    result.offset_x = (crop_center_x - metadata.pivot_x) * scale;
    // Sprite/texture Y grows upward. PNG rows and renderer screen Y grow downward.
    result.offset_y = -(crop_center_y - metadata.pivot_y) * scale;
    return result;
}

struct IconAsset
{
    std::wstring image_path;
    std::wstring outline_path;
    SpriteMetadata image_metadata;
    SpriteMetadata outline_metadata;
};

using IconAssetTable = std::unordered_map<std::uint32_t, IconAsset>;
}
