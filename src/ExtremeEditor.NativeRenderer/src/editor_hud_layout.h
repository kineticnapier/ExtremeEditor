#pragma once

#include "extreme_editor_renderer.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace ee
{
struct EditorHudButton
{
    EeEditorAction action = EE_EDITOR_ACTION_NONE;
    double value = 0.0;
    float center_x = 0.0f;
    float center_y = 0.0f;
    float half_width = 0.0f;
    float half_height = 0.0f;
    const wchar_t* label = L"";
};

inline constexpr std::size_t EditorHudButtonCount = 8;

// The native floor geometry uses this same camera zoom. Keep the HUD in
// world-relative units so both the spacing and the hit regions scale with it.
inline constexpr float EditorHudReferenceZoom = 28.0f;
inline constexpr float EditorHudMinimumZoom = 14.0f;
inline constexpr float EditorHudCornerRadius = 2.5f;
inline constexpr float EditorHudButtonHalfWidth = 8.0f;
inline constexpr float EditorHudButtonHalfHeight = 8.0f;

inline float EditorHudScaleForZoom(float zoom) noexcept
{
    if (!std::isfinite(zoom) || zoom < EditorHudMinimumZoom)
        return 0.0f;
    return zoom / EditorHudReferenceZoom;
}

// A compact eight-way keypad around the currently selected floor. The units
// here are reference-zoom pixels; both rendering and pointer hit-testing use
// this one definition (including the actual rounded corners).
inline std::array<EditorHudButton, EditorHudButtonCount> BuildEditorHudButtons(
    float anchor_x,
    float anchor_y) noexcept
{
    constexpr float diagonal_x = 31.0f;
    constexpr float diagonal_y = 31.0f;
    constexpr float axis_x = 38.0f;
    constexpr float axis_y = 41.0f;
    constexpr float hw = EditorHudButtonHalfWidth;
    constexpr float hh = EditorHudButtonHalfHeight;

    return {{
        { EE_EDITOR_ACTION_INSERT_ANGLE, 135.0, anchor_x - diagonal_x, anchor_y - diagonal_y, hw, hh, L"Q" },
        { EE_EDITOR_ACTION_INSERT_ANGLE,  90.0, anchor_x,              anchor_y - axis_y,     hw, hh, L"W" },
        { EE_EDITOR_ACTION_INSERT_ANGLE,  45.0, anchor_x + diagonal_x, anchor_y - diagonal_y, hw, hh, L"E" },
        { EE_EDITOR_ACTION_INSERT_ANGLE, 180.0, anchor_x - axis_x,     anchor_y,              hw, hh, L"A" },
        { EE_EDITOR_ACTION_INSERT_ANGLE,   0.0, anchor_x + axis_x,     anchor_y,              hw, hh, L"D" },
        { EE_EDITOR_ACTION_INSERT_ANGLE, 225.0, anchor_x - diagonal_x, anchor_y + diagonal_y, hw, hh, L"Z" },
        { EE_EDITOR_ACTION_INSERT_ANGLE, 270.0, anchor_x,              anchor_y + axis_y,     hw, hh, L"S" },
        { EE_EDITOR_ACTION_INSERT_ANGLE, 315.0, anchor_x + diagonal_x, anchor_y + diagonal_y, hw, hh, L"C" }
    }};
}

inline bool HitTestEditorHudButton(const EditorHudButton& button, float x, float y) noexcept
{
    const float dx = std::abs(x - button.center_x);
    const float dy = std::abs(y - button.center_y);
    if (dx > button.half_width || dy > button.half_height)
        return false;

    // Direct2D RoundedRect removes quarter circles from the four corners;
    // a box-only hit-test would accept clicks in these transparent areas.
    const float radius = std::min(
        EditorHudCornerRadius, std::min(button.half_width, button.half_height));
    const float corner_x = std::max(0.0f, dx - (button.half_width - radius));
    const float corner_y = std::max(0.0f, dy - (button.half_height - radius));
    return corner_x * corner_x + corner_y * corner_y <= radius * radius;
}

inline int HitTestEditorHudButtons(
    float anchor_x,
    float anchor_y,
    float zoom,
    float screen_x,
    float screen_y) noexcept
{
    const float scale = EditorHudScaleForZoom(zoom);
    if (scale == 0.0f || !std::isfinite(screen_x) || !std::isfinite(screen_y))
        return -1;

    // Undo the same anchor-relative scale transform used by Direct2D.
    const float local_x = anchor_x + (screen_x - anchor_x) / scale;
    const float local_y = anchor_y + (screen_y - anchor_y) / scale;
    const auto buttons = BuildEditorHudButtons(anchor_x, anchor_y);
    for (std::size_t i = 0; i < buttons.size(); ++i)
    {
        if (HitTestEditorHudButton(buttons[i], local_x, local_y))
            return static_cast<int>(i);
    }
    return -1;
}
}
