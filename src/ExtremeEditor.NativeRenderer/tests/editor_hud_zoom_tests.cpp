#include "editor_hud_layout.h"

#include <array>
#include <cmath>
#include <cstdio>
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
    const auto buttons = ee::BuildEditorHudButtons(anchor_x, anchor_y);

    require(std::abs(ee::EditorHudScaleForZoom(28.0f) - 1.0f) < 0.00001f,
        "initial camera zoom preserves HUD dimensions");
    require(std::abs(ee::EditorHudScaleForZoom(56.0f) - 2.0f) < 0.00001f,
        "doubling camera zoom doubles HUD dimensions");
    require(ee::EditorHudScaleForZoom(13.99f) == 0.0f,
        "too-small HUD is hidden and disabled");
    require(ee::EditorHudScaleForZoom(std::numeric_limits<float>::quiet_NaN()) == 0.0f,
        "invalid zoom is hidden");

    for (const float zoom : zooms)
    {
        const float scale = ee::EditorHudScaleForZoom(zoom);
        for (std::size_t i = 0; i < buttons.size(); ++i)
        {
            const ee::EditorHudButton& button = buttons[i];

            // Rendering applies Scale(scale, anchor) to this exact base layout.
            const float center_x = anchor_x + (button.center_x - anchor_x) * scale;
            const float center_y = anchor_y + (button.center_y - anchor_y) * scale;
            require(ee::HitTestEditorHudButtons(
                anchor_x, anchor_y, zoom, center_x, center_y) == static_cast<int>(i),
                "scaled button center must hit the same action");

            // The diamond is a rotated rectangle: its screen-space hit
            // extents are half_width*scale in Manhattan distance.
            const float outer_x = center_x + (button.half_width * scale + 2.0f);
            require(ee::HitTestEditorHudButtons(
                anchor_x, anchor_y, zoom, outer_x, center_y) != static_cast<int>(i),
                "outside the scaled diamond does not hit its action");
        }
    }
    require(ee::HitTestEditorHudButtons(
        anchor_x, anchor_y, 13.0f, anchor_x + 86.0f, anchor_y) == -1,
        "hidden direction buttons cannot intercept clicks");
    require(ee::HitTestEditorHudButtons(
        anchor_x, anchor_y, 28.0f, anchor_x, anchor_y) == -1,
        "center tile itself is not a direction button");

    if (failures != 0)
        return 1;
    std::puts("editor_hud_zoom_tests: PASS");
    return 0;
}
