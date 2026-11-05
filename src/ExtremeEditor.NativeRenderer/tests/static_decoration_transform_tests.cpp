#include "decoration_blend.h"
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

bool NearPixel(float actual, float expected) noexcept
{
    return std::abs(actual - expected) <= 0.001f;
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
    // lockScale: Screen.height * 0.001 per source pixel; chart zoom is baked
    // into scale_multiplier by the managed snapshot.
    constexpr float runtime_lock_scale = 600.0f * 0.001f;
    if (!Near(runtime_transform.m11, std::cos(0.25f) * runtime_lock_scale * 1.25f * 2.0f) ||
        !Near(runtime_transform.m22, std::cos(0.25f) * runtime_lock_scale * 1.25f * 0.5f))
    {
        return 6;
    }

    // Stock lockScale uses the sprite PPU plus the current screen height and
    // chart zoom. Phantigma floor 2135 has a 256px square at scale [2000,1000],
    // chart zoom 200 and Camera positionOffset -17 * tileSize (1.5).
    EeStaticDecoration phantigma{};
    phantigma.relative_mode = EE_DECORATION_RELATIVE_CAMERA;
    phantigma.position_x = -25.5f;
    phantigma.scale_x = 20.0f;
    phantigma.scale_y = 10.0f;
    phantigma.scale_multiplier = 0.5f; // chart zoom 200 baked by the snapshot
    phantigma.flags = EE_DECORATION_LOCK_SCALE;
    const ee::StaticDecorationScreenTransform phantigma_transform =
        ee::CalculateStaticDecorationScreenTransform(
            phantigma,
            0.0f,
            0.0f,
            0.0f,
            0.0f,
            28.0f, // stock playback base 56 / chart zoom multiplier 2
            0.0f,
            960u,
            540u,
            100.0f);
    const ee::StaticDecorationScreenRect phantigma_rect =
        ee::CalculateStaticDecorationScreenRect(phantigma_transform, 256.0f, 256.0f);
    if (!NearPixel(phantigma_transform.center_x, -744.0f) ||
        !Near(phantigma_transform.m11, 5.4f) ||
        !Near(phantigma_transform.m22, 2.7f) ||
        !NearPixel(phantigma_rect.right - phantigma_rect.left, 1382.4f) ||
        !NearPixel(phantigma_rect.bottom - phantigma_rect.top, 691.2f) ||
        !NearPixel(phantigma_rect.right, -52.8f))
    {
        return 7;
    }

    // Stock lockScale size does not depend on the current camera zoom.
    for (float camera_zoom : {14.0f, 56.0f, 112.0f})
    {
        const ee::StaticDecorationScreenTransform zoomed =
            ee::CalculateStaticDecorationScreenTransform(
                phantigma, 0.0f, 0.0f, 0.0f, 0.0f, camera_zoom, 0.0f, 960u, 540u, 100.0f);
        if (!Near(zoomed.m11, phantigma_transform.m11) ||
            !Near(zoomed.m22, phantigma_transform.m22))
        {
            return 7;
        }
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
        return 8;
    }

    D2D1_BLEND_MODE blend_mode{};
    EeStaticDecoration blend{};
    if (ee::TryGetDecorationD2DBlendMode(blend, blend_mode))
        return 9;

    struct BlendCase
    {
        std::uint32_t flags;
        D2D1_BLEND_MODE expected;
    };
    constexpr std::array<BlendCase, 6> blend_cases =
    {{
        {ee::kDecorationBlendLinearDodge, D2D1_BLEND_MODE_LINEAR_DODGE},
        {ee::kDecorationBlendMultiply, D2D1_BLEND_MODE_MULTIPLY},
        {ee::kDecorationBlendScreen, D2D1_BLEND_MODE_SCREEN},
        {ee::kDecorationBlendOverlay, D2D1_BLEND_MODE_OVERLAY},
        {ee::kDecorationBlendSoftLight, D2D1_BLEND_MODE_SOFT_LIGHT},
        {ee::kDecorationBlendDifference, D2D1_BLEND_MODE_DIFFERENCE}
    }};
    for (const BlendCase& blend_case : blend_cases)
    {
        blend.flags = blend_case.flags;
        if (!ee::TryGetDecorationD2DBlendMode(blend, blend_mode) ||
            blend_mode != blend_case.expected)
        {
            return 10;
        }
    }

    constexpr std::uint32_t masking_type_bits = 0x300u;
    constexpr std::uint32_t masking_visible_inside = 0x200u;
    blend.flags = ee::kDecorationBlendOverlay | masking_visible_inside;
    if ((blend.flags & masking_type_bits) != masking_visible_inside ||
        !ee::TryGetDecorationD2DBlendMode(blend, blend_mode) ||
        blend_mode != D2D1_BLEND_MODE_OVERLAY)
    {
        return 11;
    }

    blend.flags = ee::kDecorationBlendModeBits;
    if (ee::TryGetDecorationD2DBlendMode(blend, blend_mode))
        return 12;

    return 0;
}
