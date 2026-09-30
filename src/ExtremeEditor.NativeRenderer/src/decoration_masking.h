#pragma once

#include "extreme_editor_renderer.h"

#include <algorithm>
#include <cstdint>

namespace ee
{
constexpr std::uint32_t kDecorationMaskingTypeBits = 0x300u;
constexpr std::uint32_t kDecorationMaskingNone = 0x000u;
constexpr std::uint32_t kDecorationMaskingMask = 0x100u;
constexpr std::uint32_t kDecorationMaskingVisibleInside = 0x200u;
constexpr std::uint32_t kDecorationMaskingVisibleOutside = 0x300u;
constexpr std::uint32_t kDecorationMaskingUseDepth = 0x400u;

constexpr std::uint32_t DecorationMaskingType(const EeStaticDecoration& decoration) noexcept
{
    return decoration.flags & kDecorationMaskingTypeBits;
}

constexpr bool DecorationIsMask(const EeStaticDecoration& decoration) noexcept
{
    return DecorationMaskingType(decoration) == kDecorationMaskingMask;
}

constexpr bool DecorationIsVisibleInsideMask(const EeStaticDecoration& decoration) noexcept
{
    return DecorationMaskingType(decoration) == kDecorationMaskingVisibleInside;
}

constexpr bool DecorationIsVisibleOutsideMask(const EeStaticDecoration& decoration) noexcept
{
    return DecorationMaskingType(decoration) == kDecorationMaskingVisibleOutside;
}

// API v18 packing note: for Mask decorations only, chart_position_x/y carry
// maskingFrontDepth/maskingBackDepth. Non-mask decorations keep the diagnostic
// chart position values introduced by API v18.
constexpr int DecorationMaskFrontDepth(const EeStaticDecoration& decoration) noexcept
{
    return static_cast<int>(decoration.chart_position_x);
}

constexpr int DecorationMaskBackDepth(const EeStaticDecoration& decoration) noexcept
{
    return static_cast<int>(decoration.chart_position_y);
}

constexpr bool DecorationMaskDepthApplies(
    std::uint32_t flags,
    int mask_depth,
    int front_depth,
    int back_depth,
    int target_depth) noexcept
{
    if ((flags & kDecorationMaskingUseDepth) == 0u)
    {
        // Unity's default SpriteMask range affects renderers behind the mask.
        // ADOFAI maps decoration depth to sortingOrder as -depth, so a renderer
        // behind the mask has a larger ADOFAI depth value.
        return target_depth > mask_depth;
    }

    // ADOFAI forwards maskingFrontDepth/maskingBackDepth to SpriteMask's custom
    // sorting range. Negating Unity sorting orders reverses the interval but does
    // not change its inclusive membership in ADOFAI depth space.
    const int low = std::min(front_depth, back_depth);
    const int high = std::max(front_depth, back_depth);
    return target_depth >= low && target_depth <= high;
}

constexpr bool DecorationMaskAppliesTo(
    const EeStaticDecoration& mask,
    const EeStaticDecoration& target) noexcept
{
    return DecorationIsMask(mask) &&
           DecorationMaskDepthApplies(
               mask.flags,
               mask.depth,
               DecorationMaskFrontDepth(mask),
               DecorationMaskBackDepth(mask),
               target.depth);
}

// Default range: only decorations behind the mask participate.
static_assert(DecorationMaskDepthApplies(0u, 10, -1, -1, 11));
static_assert(!DecorationMaskDepthApplies(0u, 10, -1, -1, 10));
static_assert(!DecorationMaskDepthApplies(0u, 10, -1, -1, 9));
// Custom range follows the explicit front/back depths regardless of ordering.
static_assert(DecorationMaskDepthApplies(kDecorationMaskingUseDepth, 999, -10, 10, 0));
static_assert(DecorationMaskDepthApplies(kDecorationMaskingUseDepth, 999, 10, -10, -10));
static_assert(!DecorationMaskDepthApplies(kDecorationMaskingUseDepth, 999, -10, 10, 11));
}
