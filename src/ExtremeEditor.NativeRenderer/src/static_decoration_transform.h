#pragma once

#include "extreme_editor_renderer.h"

#include <cmath>

namespace ee
{
struct StaticDecorationScreenTransform
{
    float world_x = 0.0f;
    float world_y = 0.0f;
    float center_x = 0.0f;
    float center_y = 0.0f;
    float m11 = 1.0f;
    float m12 = 0.0f;
    float m21 = 0.0f;
    float m22 = 1.0f;
};

struct StaticDecorationScreenRect
{
    float left = 0.0f;
    float top = 0.0f;
    float right = 0.0f;
    float bottom = 0.0f;
};

inline StaticDecorationScreenRect CalculateStaticDecorationScreenRect(
    const StaticDecorationScreenTransform& transform,
    float bitmap_width,
    float bitmap_height) noexcept
{
    const float half_width =
        (std::abs(transform.m11) * bitmap_width +
         std::abs(transform.m21) * bitmap_height) * 0.5f;
    const float half_height =
        (std::abs(transform.m12) * bitmap_width +
         std::abs(transform.m22) * bitmap_height) * 0.5f;
    return
    {
        transform.center_x - half_width,
        transform.center_y - half_height,
        transform.center_x + half_width,
        transform.center_y + half_height
    };
}

inline StaticDecorationScreenTransform CalculateStaticDecorationScreenTransform(
    const EeStaticDecoration& decoration,
    float anchor_x,
    float anchor_y,
    float camera_x,
    float camera_y,
    float zoom,
    float camera_rotation,
    std::uint32_t width,
    std::uint32_t height,
    float pixels_per_unit,
    float parent_rotation = 0.0f,
    float parent_scale_x = 1.0f,
    float parent_scale_y = 1.0f,
    float camera_start_x = 0.0f,
    float camera_start_y = 0.0f) noexcept
{
    const bool stick_to_floor = (decoration.flags & EE_DECORATION_STICK_TO_FLOOR) != 0u;
    const bool lock_rotation = (decoration.flags & EE_DECORATION_LOCK_ROTATION) != 0u;
    const bool lock_scale = (decoration.flags & EE_DECORATION_LOCK_SCALE) != 0u;
    const bool camera_relative =
        decoration.relative_mode == EE_DECORATION_RELATIVE_CAMERA ||
        decoration.relative_mode == EE_DECORATION_RELATIVE_CAMERA_ASPECT;

    float world_rotation = decoration.rotation_radians;
    if (stick_to_floor)
        world_rotation += parent_rotation;
    else if (lock_rotation && !camera_relative)
        world_rotation += camera_rotation;

    const float local_c = std::cos(world_rotation);
    const float local_s = std::sin(world_rotation);
    const float rotated_pivot_x =
        local_c * decoration.pivot_offset_x - local_s * decoration.pivot_offset_y;
    const float rotated_pivot_y =
        local_s * decoration.pivot_offset_x + local_c * decoration.pivot_offset_y;

    float world_x = anchor_x + decoration.position_x + rotated_pivot_x;
    float world_y = anchor_y + decoration.position_y + rotated_pivot_y;
    if (decoration.parallax_x != 0.0f || decoration.parallax_y != 0.0f)
    {
        world_x += (camera_x - camera_start_x) * decoration.parallax_x + decoration.parallax_offset_x;
        world_y += (camera_y - camera_start_y) * decoration.parallax_y + decoration.parallax_offset_y;
    }
    const float dx = world_x - camera_x;
    const float dy = world_y - camera_y;
    const float camera_c = std::cos(camera_rotation);
    const float camera_s = std::sin(camera_rotation);
    const float view_x = camera_c * dx + camera_s * dy;
    const float view_y = -camera_s * dx + camera_c * dy;

    StaticDecorationScreenTransform result;
    result.world_x = world_x;
    result.world_y = world_y;
    if (camera_relative)
    {
        float relative_x = decoration.position_x + decoration.pivot_offset_x;
        const float relative_y = decoration.position_y + decoration.pivot_offset_y;
        if (decoration.relative_mode == EE_DECORATION_RELATIVE_CAMERA_ASPECT && width > 0u)
            relative_x *= static_cast<float>(height) / static_cast<float>(width);
        const float normalized_x = relative_x / 20.0f + 0.5f;
        const float normalized_y = relative_y / 20.0f + 0.5f;
        result.center_x = normalized_x * static_cast<float>(width);
        result.center_y = (1.0f - normalized_y) * static_cast<float>(height);
    }
    else
    {
        result.center_x = view_x * zoom + static_cast<float>(width) * 0.5f;
        result.center_y = -view_y * zoom + static_cast<float>(height) * 0.5f;
    }

    // ADOFAI rotation is positive counter-clockwise in Unity world space.
    // Direct2D's Y-down screen transform therefore uses camera - decoration.
    const float placement_camera_rotation = camera_relative ? 0.0f : camera_rotation;
    const float screen_angle = placement_camera_rotation - world_rotation;
    const float c = std::cos(screen_angle);
    const float s = std::sin(screen_angle);
    const float scale_zoom = lock_scale ? pixels_per_unit : zoom;
    const float floor_scale_x = stick_to_floor ? parent_scale_x : 1.0f;
    const float floor_scale_y = stick_to_floor ? parent_scale_y : 1.0f;
    const float sx = scale_zoom * decoration.scale_x * decoration.scale_multiplier * floor_scale_x /
        pixels_per_unit;
    const float sy = scale_zoom * decoration.scale_y * decoration.scale_multiplier * floor_scale_y /
        pixels_per_unit;
    result.m11 = sx * c;
    result.m12 = sx * s;
    result.m21 = -sy * s;
    result.m22 = sy * c;
    return result;
}

inline bool StaticDecorationDrawOrderLess(
    const EeStaticDecoration& left,
    const EeStaticDecoration& right) noexcept
{
    // Unity sortingOrder is -depth: larger ADOFAI depth is drawn first.
    if (left.depth != right.depth)
        return left.depth > right.depth;
    return left.source_index < right.source_index;
}
}
