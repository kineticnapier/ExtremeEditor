#include "editor_hud_layout.h"

#include <array>
#include <cmath>
#include <cstdio>
#include <cwchar>
#include <limits>

int main()
{
    int failures = 0;
    const auto require = [&failures](bool ok, const char* name)
    {
        if (!ok)
        {
            std::fprintf(stderr, "FAIL: %s\n", name);
            ++failures;
        }
    };

    constexpr float anchor_x = 430.0f;
    constexpr float anchor_y = 270.0f;
    constexpr std::array<float, 5> zooms{14.0f, 28.0f, 56.0f, 112.0f, 400.0f};
    constexpr std::array<const wchar_t*, 8> labels{
        L"Q", L"W", L"E", L"A", L"D", L"Z", L"S", L"C"};
    constexpr std::array<double, 8> angles{
        135.0, 90.0, 45.0, 180.0, 0.0, 225.0, 270.0, 315.0};
    const auto buttons = ee::BuildEditorHudButtons(anchor_x, anchor_y);

    require(std::abs(ee::EditorHudScaleForZoom(28.0f) - 1.0f) < 0.00001f,
        "initial camera zoom preserves HUD dimensions");
    require(std::abs(ee::EditorHudScaleForZoom(56.0f) - 2.0f) < 0.00001f,
        "doubling camera zoom doubles HUD dimensions");
    require(ee::EditorHudScaleForZoom(13.99f) == 0.0f,
        "too-small HUD is hidden and disabled");
    require(ee::EditorHudScaleForZoom(std::numeric_limits<float>::quiet_NaN()) == 0.0f,
        "invalid zoom is hidden");

    for (std::size_t i = 0; i < buttons.size(); ++i)
    {
        require(std::wcscmp(buttons[i].label, labels[i]) == 0,
            "keycap must retain the expected ADOFAI directional key");
        require(buttons[i].value == angles[i],
            "keycap must retain the existing ADOFAI angle command");
        require(buttons[i].half_width <= 8.0f &&
                buttons[i].half_height <= 8.0f,
            "keycaps should remain at most 16px across at reference zoom");
        require(std::abs(buttons[i].center_x - anchor_x) <= 38.0f &&
                std::abs(buttons[i].center_y - anchor_y) <= 41.0f,
            "keycaps should sit immediately around the selected tile");
    }

    // The user-visible layout was still too large at a typical working zoom.
    // At zoom 48 the cap must be ~27px rather than the former ~48px.
    const float working_zoom = 48.0f;
    const float expected_cap_pixels = 2.0f * ee::EditorHudButtonHalfWidth *
        ee::EditorHudScaleForZoom(working_zoom);
    require(expected_cap_pixels >= 26.0f && expected_cap_pixels <= 28.0f,
        "typical zoom keycaps must fit the compact 26-28px design");

    for (float zoom : zooms)
    {
        const float scale = ee::EditorHudScaleForZoom(zoom);
        for (std::size_t i = 0; i < buttons.size(); ++i)
        {
            const auto& button = buttons[i];
            // Direct2D scales about the selected tile center.
            const float center_x = anchor_x + (button.center_x - anchor_x) * scale;
            const float center_y = anchor_y + (button.center_y - anchor_y) * scale;
            const auto hit = [&](float offset_x, float offset_y)
            {
                return ee::HitTestEditorHudButtons(
                    anchor_x, anchor_y, zoom,
                    center_x + offset_x * scale, center_y + offset_y * scale);
            };

            require(hit(0.0f, 0.0f) == static_cast<int>(i),
                "scaled keycap center must activate its own command");
            require(hit(button.half_width + 2.0f, 0.0f) != static_cast<int>(i),
                "outside a zoom-scaled keycap must not activate its command");
            require(hit(button.half_width - 0.1f, button.half_height - 0.1f) !=
                    static_cast<int>(i),
                "rounded transparent corner must not be clickable");

            const float corner = ee::EditorHudCornerRadius * 0.70f;
            const float inside_x = button.half_width - ee::EditorHudCornerRadius + corner;
            const float inside_y = button.half_height - ee::EditorHudCornerRadius + corner;
            require(hit(inside_x, inside_y) == static_cast<int>(i),
                "interior of rounded corner remains clickable");
        }
    }

    require(ee::HitTestEditorHudButtons(
        anchor_x, anchor_y, 13.0f, anchor_x + 49.0f, anchor_y) == -1,
        "hidden direction buttons must not intercept clicks");
    require(ee::HitTestEditorHudButtons(
        anchor_x, anchor_y, 28.0f, anchor_x, anchor_y) == -1,
        "selected tile itself must remain unobstructed");

    if (failures != 0)
        return 1;
    std::puts("editor_hud_zoom_tests: PASS");
    return 0;
}
