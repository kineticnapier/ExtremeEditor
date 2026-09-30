#pragma once

#include "extreme_editor_renderer.h"

#include <cstdint>
#include <d2d1effects.h>

namespace ee
{
constexpr std::uint32_t kDecorationBlendModeShift = 12u;
constexpr std::uint32_t kDecorationBlendModeBits = 0x7000u;
constexpr std::uint32_t kDecorationBlendNone = 0x0000u;
constexpr std::uint32_t kDecorationBlendLinearDodge = 0x1000u;
constexpr std::uint32_t kDecorationBlendMultiply = 0x2000u;
constexpr std::uint32_t kDecorationBlendScreen = 0x3000u;
constexpr std::uint32_t kDecorationBlendOverlay = 0x4000u;
constexpr std::uint32_t kDecorationBlendSoftLight = 0x5000u;
constexpr std::uint32_t kDecorationBlendDifference = 0x6000u;

constexpr std::uint32_t DecorationBlendMode(const EeStaticDecoration& decoration) noexcept
{
    return decoration.flags & kDecorationBlendModeBits;
}

inline bool TryGetDecorationD2DBlendMode(
    const EeStaticDecoration& decoration,
    D2D1_BLEND_MODE& mode) noexcept
{
    switch (DecorationBlendMode(decoration))
    {
    case kDecorationBlendLinearDodge:
        mode = D2D1_BLEND_MODE_LINEAR_DODGE;
        return true;
    case kDecorationBlendMultiply:
        mode = D2D1_BLEND_MODE_MULTIPLY;
        return true;
    case kDecorationBlendScreen:
        mode = D2D1_BLEND_MODE_SCREEN;
        return true;
    case kDecorationBlendOverlay:
        mode = D2D1_BLEND_MODE_OVERLAY;
        return true;
    case kDecorationBlendSoftLight:
        mode = D2D1_BLEND_MODE_SOFT_LIGHT;
        return true;
    case kDecorationBlendDifference:
        mode = D2D1_BLEND_MODE_DIFFERENCE;
        return true;
    default:
        return false;
    }
}

inline bool DecorationHasCustomBlend(const EeStaticDecoration& decoration) noexcept
{
    D2D1_BLEND_MODE ignored{};
    return TryGetDecorationD2DBlendMode(decoration, ignored);
}
}
