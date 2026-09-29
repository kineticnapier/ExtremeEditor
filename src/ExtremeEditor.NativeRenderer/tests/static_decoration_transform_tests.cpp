#include "static_decoration_transform.h"

#include <algorithm>
#include <array>
#include <cmath>

namespace
{
constexpr float Pi = 3.14159265358979323846f;

bool Near(float actual, float expected) noexcept
{
    return std::abs(actual - expected) <= 0.0001f;
}
}

int main()
{
    EeStaticDecoration decoration{};
    decoration.position_x = 2.0f;
    decoration.position_y = 3.0f;
    decoration.pivot_offset_x = 1.0f;
    decoration.pivot_offset_y = 0.0f;
    decoration.rotation_radians = Pi * 0.5f;
    decoration.scale_x = 1.0f;
    decoration.scale_y = 1.0f;
    decoration.scale_multiplier = 1.0f;

    ee::StaticDecorationScreenTransform unrotated_camera =
        ee::CalculateStaticDecorationScreenTransform(
            decoration,
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            100.0f,
            0.0f,
            800u,
            600u,
            100.0f);
    if (!Near(unrotated_camera.center_x, 600.0f) ||
        !Near(unrotated_camera.center_y, -100.0f) ||
        !Near(unrotated_camera.m11, 0.0f) ||
        !Near(unrotated_camera.m12, -1.0f) ||
        !Near(unrotated_camera.m21, 1.0f) ||
        !Near(unrotated_camera.m22, 0.0f))
    {
        return 1;
    }

    ee::StaticDecorationScreenTransform rotated_camera =
        ee::CalculateStaticDecorationScreenTransform(
            decoration,
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            100.0f,
            Pi * 0.5f,
            800u,
            600u,
            100.0f);
    if (!Near(rotated_camera.center_x, 800.0f) ||
        !Near(rotated_camera.center_y, 500.0f) ||
        !Near(rotated_camera.m11, 1.0f) ||
        !Near(rotated_camera.m12, 0.0f) ||
        !Near(rotated_camera.m21, 0.0f) ||
        !Near(rotated_camera.m22, 1.0f))
    {
        return 2;
    }

    EeStaticDecoration negative_scale = decoration;
    negative_scale.rotation_radians = 0.0f;
    negative_scale.scale_x = -1.25f;
    negative_scale.scale_y = 0.8f;
    ee::StaticDecorationScreenTransform flipped =
        ee::CalculateStaticDecorationScreenTransform(
            negative_scale,
            10.0f,
            -2.0f,
            0.0f,
            0.0f,
            100.0f,
            0.0f,
            800u,
            600u,
            100.0f);
    if (!Near(flipped.center_x, 1700.0f) ||
        !Near(flipped.center_y, 200.0f) ||
        !Near(flipped.m11, -1.25f) ||
        !Near(flipped.m22, 0.8f))
    {
        return 3;
    }

    std::array<EeStaticDecoration, 3> ordered{};
    ordered[0].depth = -5;
    ordered[0].source_index = 0;
    ordered[1].depth = 10;
    ordered[1].source_index = 2;
    ordered[2].depth = 10;
    ordered[2].source_index = 1;
    std::stable_sort(ordered.begin(), ordered.end(), ee::StaticDecorationDrawOrderLess);
    if (ordered[0].source_index != 1 ||
        ordered[1].source_index != 2 ||
        ordered[2].source_index != 0)
    {
        return 4;
    }

    EeStaticDecoration camera{};
    camera.relative_mode = EE_DECORATION_RELATIVE_CAMERA_ASPECT;
    camera.position_x = 4.0f;
    camera.position_y = -2.0f;
    camera.pivot_offset_x = 1.0f;
    camera.pivot_offset_y = 2.0f;
    camera.scale_x = 1.0f;
    camera.scale_y = 1.0f;
    camera.scale_multiplier = 1.0f;
    const ee::StaticDecorationScreenTransform camera_transform =
        ee::CalculateStaticDecorationScreenTransform(
            camera,
            0.0f,
            0.0f,
            50.0f,
            -20.0f,
            100.0f,
            Pi * 0.25f,
            800u,
            400u,
            100.0f);
    if (!Near(camera_transform.center_x, 500.0f) ||
        !Near(camera_transform.center_y, 200.0f))
    {
        return 5;
    }

    EeStaticDecoration runtime = decoration;
    runtime.rotation_radians = 0.0f;
    runtime.pivot_offset_x = 0.0f;
    runtime.flags = EE_DECORATION_LOCK_ROTATION | EE_DECORATION_LOCK_SCALE |
        EE_DECORATION_STICK_TO_FLOOR;
    runtime.scale_multiplier = 1.25f;
    const ee::StaticDecorationScreenTransform runtime_transform =
        ee::CalculateStaticDecorationScreenTransform(
            runtime,
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            240.0f,
            0.5f,
            800u,
            600u,
            100.0f,
            0.25f,
            2.0f,
            0.5f);
    if (!Near(runtime_transform.m11, std::cos(0.25f) * 2.5f) ||
        !Near(runtime_transform.m22, std::cos(0.25f) * 0.625f))
    {
        return 6;
    }

    ee::StaticDecorationScreenTransform rect_transform{};
    rect_transform.center_x = 400.0f;
    rect_transform.center_y = 300.0f;
    rect_transform.m11 = 1.0f;
    rect_transform.m12 = 0.0f;
    rect_transform.m21 = 0.0f;
    rect_transform.m22 = 1.0f;
    const ee::StaticDecorationScreenRect screen_rect =
        ee::CalculateStaticDecorationScreenRect(rect_transform, 800.0f, 600.0f);
    if (!Near(screen_rect.left, 0.0f) ||
        !Near(screen_rect.top, 0.0f) ||
        !Near(screen_rect.right, 800.0f) ||
        !Near(screen_rect.bottom, 600.0f))
    {
        return 7;
    }

    return 0;
}
