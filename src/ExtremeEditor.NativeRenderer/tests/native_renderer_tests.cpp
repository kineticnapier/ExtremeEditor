#include "extreme_editor_renderer.h"
#include "icon_assets.h"

#include <cmath>
#include <iostream>

int main()
{
    if (ee_renderer_get_api_version() != EE_RENDERER_API_VERSION)
    {
        std::cerr << "API version mismatch.\n";
        return 1;
    }

    EeAbiInfo info{};
    info.struct_size = sizeof(EeAbiInfo);
    if (ee_renderer_get_abi_info(&info) != EE_OK)
    {
        std::cerr << "ABI info query failed.\n";
        return 1;
    }

    if (info.api_version != EE_RENDERER_API_VERSION)
    {
        std::cerr << "ABI info reported wrong API version.\n";
        return 1;
    }

    if (info.floor_size != sizeof(EeFloor) ||
        info.clock_size != sizeof(EePlaybackTiming) ||
        info.diagnostics_size != sizeof(EeRendererDiagnostics) ||
        info.sprite_metadata_size != sizeof(EeSpriteMetadata) ||
        info.static_decoration_size != sizeof(EeStaticDecoration) ||
        info.floor_icon_state_size != sizeof(EeFloorIconState) ||
        info.track_visual_event_size != sizeof(EeTrackVisualEvent))
    {
        std::cerr << "ABI struct size mismatch.\n";
        return 1;
    }

    EeFloorIconState icon_state{};
    if (ee_renderer_update_floor_icons(nullptr, 0u, &icon_state, 1u) != EE_ERROR_INVALID_ARGUMENT)
    {
        std::cerr << "Floor icon update accepted a null renderer.\n";
        return 1;
    }

    EeAbiInfo bad{};
    if (ee_renderer_get_abi_info(&bad) != EE_ERROR_ABI_MISMATCH)
    {
        std::cerr << "ABI size mismatch was not rejected.\n";
        return 1;
    }

    ee::SpriteMetadata portal;
    portal.valid = true;
    portal.sprite_rect_width = 256.0f;
    portal.sprite_rect_height = 256.0f;
    portal.texture_rect_x = 44.0f;
    portal.texture_rect_y = 43.0f;
    portal.texture_rect_width = 169.0f;
    portal.texture_rect_height = 169.0f;
    portal.pivot_x = 128.0f;
    portal.pivot_y = 128.0f;
    portal.pixels_per_unit = 290.0f;

    const ee::SpriteDrawLayout portal_layout =
        ee::CalculateSpriteDrawLayout(portal, 169u, 169u, 78.0f);
    constexpr float epsilon = 0.00001f;
    if (std::abs(portal_layout.width - 78.0f * 169.0f / 256.0f) > epsilon ||
        std::abs(portal_layout.height - 78.0f * 169.0f / 256.0f) > epsilon ||
        std::abs(portal_layout.offset_x - 78.0f * 0.5f / 256.0f) > epsilon ||
        std::abs(portal_layout.offset_y - 78.0f * 0.5f / 256.0f) > epsilon)
    {
        std::cerr << "Sprite crop layout did not preserve full-canvas semantics.\n";
        return 1;
    }

    constexpr float portal_zooms[] = {50.0f, 100.0f, 200.0f, 500.0f};
    for (const float zoom : portal_zooms)
    {
        const float requested_size = zoom * 0.78f;
        const float expected_visible_size = requested_size * 169.0f / 256.0f;
        const ee::SpriteDrawLayout zoom_layout =
            ee::CalculateSpriteDrawLayout(portal, 169u, 169u, requested_size);
        if (std::abs(zoom_layout.width - expected_visible_size) > epsilon ||
            std::abs(zoom_layout.height - expected_visible_size) > epsilon)
        {
            std::cerr << "Portal Sprite size was not zoom-proportional at zoom " << zoom
                      << ": expected " << expected_visible_size
                      << ", actual " << zoom_layout.width << ".\n";
            return 1;
        }
    }

    const ee::SpriteDrawLayout fallback_layout =
        ee::CalculateSpriteDrawLayout({}, 169u, 169u, 78.0f);
    if (std::abs(fallback_layout.width - 78.0f) > epsilon ||
        std::abs(fallback_layout.height - 78.0f) > epsilon ||
        std::abs(fallback_layout.offset_x) > epsilon ||
        std::abs(fallback_layout.offset_y) > epsilon)
    {
        std::cerr << "Missing Sprite metadata did not preserve legacy sizing.\n";
        return 1;
    }

    std::cout << "PASS: native renderer ABI smoke test.\n";
    return 0;
}
