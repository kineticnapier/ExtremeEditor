#include "extreme_editor_renderer.h"
#include "extreme_editor_headless.h"
#include "icon_assets.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <vector>

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
        info.track_visual_event_size != sizeof(EeTrackVisualEvent) ||
        info.track_animation_segment_size != sizeof(EeTrackAnimationSegment) ||
        info.track_animation_timing_size != sizeof(EeTrackAnimationTiming))
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

    // DMDOD B0 contract: no HWND, explicit scene/visual clocks, tightly packed
    // 320x180 RGB888 output, and order-independent deterministic seeking.
    EeRendererCreateInfo create_info{};
    create_info.struct_size = sizeof(EeRendererCreateInfo);
    create_info.width = 320u;
    create_info.height = 180u;

    EeRendererHandle headless = nullptr;
    if (ee_renderer_create_headless(&create_info, &headless) != EE_OK || headless == nullptr)
    {
        std::cerr << "Headless renderer creation failed.\n";
        return 1;
    }

    const auto destroy_headless = [&headless]()
    {
        ee_renderer_destroy(headless);
        headless = nullptr;
    };

    if (ee_renderer_get_child_hwnd(headless) != nullptr)
    {
        std::cerr << "Headless renderer unexpectedly created a child HWND.\n";
        destroy_headless();
        return 1;
    }

    EePoint points[] =
    {
        {-0.55f, -0.55f},
        { 0.55f, -0.55f},
        { 0.55f,  0.55f},
        {-0.55f,  0.55f}
    };
    EeGeometry geometry{};
    geometry.point_offset = 0u;
    geometry.point_count = 4u;

    EeFloor floors[2]{};
    for (std::uint32_t i = 0; i < 2u; ++i)
    {
        floors[i].x = static_cast<float>(i) * 1.5f;
        floors[i].y = 0.0f;
        floors[i].entry_angle = 0.0f;
        floors[i].geometry_id = 0u;
        floors[i].icon_id = EE_ICON_NONE;
        floors[i].track_primary_color = 0xff0000ffu;   // red
        floors[i].track_secondary_color = 0xffff0000u; // blue
        floors[i].track_visual_flags = EE_TRACK_VISUAL_ENABLED | 2u; // Glow
        floors[i].track_anim_duration = 1.0f;
        floors[i].track_pulse_length = 1u;
        floors[i].transform_scale_x = 1.0f;
        floors[i].transform_scale_y = 1.0f;
        floors[i].transform_opacity = 1.0f;
        floors[i].track_extend_anim = 1.0f;
    }

    if (ee_renderer_set_level(
            headless,
            floors,
            2u,
            &geometry,
            1u,
            points,
            4u,
            -1.0f,
            -1.0f,
            2.5f,
            1.0f) != EE_OK)
    {
        std::cerr << "Headless level upload failed.\n";
        destroy_headless();
        return 1;
    }

    EePlaybackTiming timings[2]{};
    timings[0].entry_time = 0.0;
    timings[0].exit_time = 1.0;
    timings[0].entry_angle = 0.0f;
    timings[0].angle_moved = 3.14159265358979323846f;
    timings[1].entry_time = 1.0;
    timings[1].exit_time = 2.0;
    timings[1].entry_angle = 3.14159265358979323846f;
    timings[1].angle_moved = 3.14159265358979323846f;
    if (ee_renderer_set_playback_timeline(headless, timings, 2u) != EE_OK)
    {
        std::cerr << "Headless playback upload failed.\n";
        destroy_headless();
        return 1;
    }

    constexpr std::uint32_t row_stride = 320u * 3u;
    constexpr std::uint32_t frame_bytes = row_stride * 180u;
    std::vector<std::uint8_t> first(frame_bytes);
    std::vector<std::uint8_t> same(frame_bytes);
    std::vector<std::uint8_t> changed(frame_bytes);
    std::vector<std::uint8_t> rewind(frame_bytes);

    if (ee_renderer_render_rgb(headless, 0.25, 0.25, first.data(), frame_bytes, row_stride) != EE_OK ||
        ee_renderer_render_rgb(headless, 0.25, 0.25, same.data(), frame_bytes, row_stride) != EE_OK)
    {
        std::cerr << "Headless RGB render failed.\n";
        destroy_headless();
        return 1;
    }

    if (first != same)
    {
        std::cerr << "Identical explicit times produced different RGB frames.\n";
        destroy_headless();
        return 1;
    }

    bool has_scene_signal = false;
    for (std::size_t i = 0; i + 2u < first.size(); i += 3u)
    {
        if (first[i] > 80u || first[i + 2u] > 80u)
        {
            has_scene_signal = true;
            break;
        }
    }
    if (!has_scene_signal)
    {
        std::cerr << "Headless RGB frame did not contain rendered scene signal.\n";
        destroy_headless();
        return 1;
    }

    // Glow uses a cosine loop, so quarter and three-quarter phases are equal.
    // Compare quarter against half phase to prove visual_time reaches pixels.
    if (ee_renderer_render_rgb(headless, 0.25, 0.50, changed.data(), frame_bytes, row_stride) != EE_OK)
    {
        std::cerr << "Headless visual-time render failed.\n";
        destroy_headless();
        return 1;
    }
    if (first == changed)
    {
        std::cerr << "visual_time did not affect animated track pixels.\n";
        destroy_headless();
        return 1;
    }

    if (ee_renderer_render_rgb(headless, 1.25, 1.25, changed.data(), frame_bytes, row_stride) != EE_OK ||
        ee_renderer_render_rgb(headless, 0.25, 0.25, rewind.data(), frame_bytes, row_stride) != EE_OK)
    {
        std::cerr << "Headless seek reconstruction failed.\n";
        destroy_headless();
        return 1;
    }
    if (first != rewind)
    {
        std::cerr << "Rewinding to the same explicit time changed RGB output.\n";
        destroy_headless();
        return 1;
    }

    destroy_headless();
    std::cout << "PASS: native renderer ABI + deterministic headless RGB smoke test.\n";
    return 0;
}
