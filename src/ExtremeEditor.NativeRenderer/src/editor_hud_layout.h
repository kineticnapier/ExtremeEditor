#pragma once

#include "extreme_editor_renderer.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace ee
{
enum class EditorHudShape : std::uint8_t
{
    Diamond,
    RoundedRect
};

struct EditorHudButton
{
    EeEditorAction action = EE_EDITOR_ACTION_NONE;
    double value = 0.0;
    float center_x = 0.0f;
    float center_y = 0.0f;
    float half_width = 0.0f;
    float half_height = 0.0f;
    EditorHudShape shape = EditorHudShape::Diamond;
    const wchar_t* label = L"";
    const wchar_t* sublabel = L"";
    bool destructive = false;
    bool accent = false;
};

inline constexpr std::size_t EditorHudButtonCount = 8;

inline std::array<EditorHudButton, EditorHudButtonCount> BuildEditorHudButtons(
    float anchor_x,
    float anchor_y) noexcept
{
    constexpr float diagonal = 25.0f;
    constexpr float x_near = 62.0f;
    constexpr float y_near = 58.0f;
    constexpr float x_axis = 86.0f;
    constexpr float y_axis = 80.0f;

    return {{
        { EE_EDITOR_ACTION_INSERT_ANGLE, 135.0, anchor_x - x_near, anchor_y - y_near, diagonal, diagonal, EditorHudShape::Diamond, L"Q", L"135\x00B0", false, false },
        { EE_EDITOR_ACTION_INSERT_ANGLE,  90.0, anchor_x,          anchor_y - y_axis, diagonal, diagonal, EditorHudShape::Diamond, L"W", L"90\x00B0", false, false },
        { EE_EDITOR_ACTION_INSERT_ANGLE,  45.0, anchor_x + x_near, anchor_y - y_near, diagonal, diagonal, EditorHudShape::Diamond, L"E", L"45\x00B0", false, false },
        { EE_EDITOR_ACTION_INSERT_ANGLE, 180.0, anchor_x - x_axis, anchor_y,          diagonal, diagonal, EditorHudShape::Diamond, L"A", L"180\x00B0", false, false },
        { EE_EDITOR_ACTION_INSERT_ANGLE,   0.0, anchor_x + x_axis, anchor_y,          diagonal, diagonal, EditorHudShape::Diamond, L"D", L"0\x00B0", false, false },
        { EE_EDITOR_ACTION_INSERT_ANGLE, 225.0, anchor_x - x_near, anchor_y + y_near, diagonal, diagonal, EditorHudShape::Diamond, L"Z", L"225\x00B0", false, false },
        { EE_EDITOR_ACTION_INSERT_ANGLE, 270.0, anchor_x,          anchor_y + y_axis, diagonal, diagonal, EditorHudShape::Diamond, L"S", L"270\x00B0", false, false },
        { EE_EDITOR_ACTION_INSERT_ANGLE, 315.0, anchor_x + x_near, anchor_y + y_near, diagonal, diagonal, EditorHudShape::Diamond, L"C", L"315\x00B0", false, false }
    }};
}

inline bool HitTestEditorHudButton(const EditorHudButton& button, float x, float y) noexcept
{
    const float dx = std::abs(x - button.center_x);
    const float dy = std::abs(y - button.center_y);
    if (button.shape == EditorHudShape::Diamond)
    {
        const float radius = std::max(button.half_width, button.half_height);
        return dx + dy <= radius;
    }

    return dx <= button.half_width && dy <= button.half_height;
}
}
