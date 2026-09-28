#pragma once

#include "extreme_editor_renderer.h"

#include <cmath>

namespace ee
{
struct StaticDecorationScreenTransform
{
    float center_x = 0.0f;
    float center_y = 0.0f;
    float m11 = 1.0f;
    float m12 = 0.0f;
    float m21 = 0.0f;
    float m22 = 1.0f;
};

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
    float pixels_per_unit) noexcept
{
    const float local_c = std::cos(decoration.rotation_radians);
    const float local_s = std::sin(decoration.rotation_radians);
    const float rotated_pivot_x =
        local_c * decoration.pivot_offset_x - local_s * decoration.pivot_offset_y;
    const float rotated_pivot_y =
        local_s * decoration.pivot_offset_x + local_c * decoration.pivot_offset_y;

    const float world_x = anchor_x + decoration.position_x + rotated_pivot_x;
    const float world_y = anchor_y + decoration.position_y + rotated_pivot_y;
    const float dx = world_x - camera_x;
    const float dy = world_y - camera_y;
    const float camera_c = std::cos(camera_rotation);
    const float camera_s = std::sin(camera_rotation);
    const float view_x = camera_c * dx + camera_s * dy;
    const float view_y = -camera_s * dx + camera_c * dy;

    StaticDecorationScreenTransform result;
    result.center_x = view_x * zoom + static_cast<float>(width) * 0.5f;
    result.center_y = -view_y * zoom + static_cast<float>(height) * 0.5f;

    // ADOFAI rotation is positive counter-clockwise in Unity world space.
    // Direct2D's Y-down screen transform therefore uses camera - decoration.
    const float screen_angle = camera_rotation - decoration.rotation_radians;
    const float c = std::cos(screen_angle);
    const float s = std::sin(screen_angle);
    const float sx = zoom * decoration.scale_x / pixels_per_unit;
    const float sy = zoom * decoration.scale_y / pixels_per_unit;
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
