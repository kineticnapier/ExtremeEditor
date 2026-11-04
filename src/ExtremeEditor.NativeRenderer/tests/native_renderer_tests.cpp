#include "extreme_editor_renderer.h"
#include "extreme_editor_headless.h"
#include "icon_assets.h"
#include "screen_filter.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <vector>

int main()
{
    {
        EeFilterEvent events[2]{};
        events[0].start_time = 0.0;
        events[0].duration_seconds = 4.0;
        events[0].filter = ee::FilterGrayscale;
        events[0].target_intensity = 1.0f;
        events[0].enabled = 1u;
        events[1].start_time = 2.0;
        events[1].duration_seconds = 2.0;
        events[1].filter = ee::FilterGrayscale;
        events[1].target_intensity = 0.0f;
        events[1].enabled = 1u;
        events[1].source_index = 1u;
        const ee::FilterVisualState interrupted = ee::ResolveScreenFilters(
            events, 2u, 3.0, nullptr);
        if (!interrupted.entries[ee::FilterGrayscale].enabled ||
            std::abs(interrupted.entries[ee::FilterGrayscale].intensity - 0.5f) > 0.00001f)
        {
            std::cerr << "SetFilter tween interruption did not complete the prior tween.\n";
            return 1;
        }
    }

    // Finite but opposite-sign SetFilter targets must not overflow during
    // interpolation. The old float subtraction yielded Infinity (and NaN
    // at progress zero), even though the expected midpoint is zero.
    {
        EeFilterEvent extreme[2]{};
        extreme[0].start_time = 0.0;
        extreme[0].filter = ee::FilterGrayscale;
        extreme[0].target_intensity = -2e38f;
        extreme[0].enabled = 1u;
        extreme[1].start_time = 1.0;
        extreme[1].duration_seconds = 2.0;
        extreme[1].filter = ee::FilterGrayscale;
        extreme[1].target_intensity = 2e38f;
        extreme[1].enabled = 1u;
        extreme[1].source_index = 1u;

        const float at_start = ee::ResolveScreenFilters(
            extreme, 2u, 1.0, nullptr).entries[ee::FilterGrayscale].intensity;
        const float at_midpoint = ee::ResolveScreenFilters(
            extreme, 2u, 2.0, nullptr).entries[ee::FilterGrayscale].intensity;
        const float at_end = ee::ResolveScreenFilters(
            extreme, 2u, 3.0, nullptr).entries[ee::FilterGrayscale].intensity;
        if (!std::isfinite(at_start) || !std::isfinite(at_midpoint) ||
            !std::isfinite(at_end) ||
            std::abs(at_start + 2e38f) > 1e30f ||
            std::abs(at_midpoint) > 1e30f ||
            std::abs(at_end - 2e38f) > 1e30f)
        {
            std::cerr << "SetFilter native tween overflowed finite endpoints.\\n";
            return 1;
        }
    }

    // The reverse-direction HUD setter was added in v24. Later API versions
    // are additive and must continue to satisfy this ABI regression.
    constexpr uint32_t reverse_direction_hud_minimum_api_version = 24u;
    if (EE_RENDERER_API_VERSION < reverse_direction_hud_minimum_api_version)
    {
        std::cerr << "Reverse-direction HUD ABI requires API version 24 or newer.\n";
        return 1;
    }

    // Link/ABI smoke test for the new optional reverse-direction HUD setter.
    // A null handle is explicitly a no-op and must never crash.
    ee_renderer_set_editor_hud_delete_button(nullptr, -1);

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

    const int32_t clean_view_selection[] = {1, 2};
    ee_renderer_set_selection(headless, clean_view_selection, 2u, 1);
    ee_renderer_set_clean_view(headless, 1);
    if (ee_renderer_get_selected_floor(headless) != 1)
    {
        std::cerr << "Clean view changed native selection state.\n";
        destroy_headless();
        return 1;
    }
    ee_renderer_set_clean_view(headless, 0);
    if (ee_renderer_get_selected_floor(headless) != 1)
    {
        std::cerr << "Leaving clean view changed native selection state.\n";
        destroy_headless();
        return 1;
    }
    ee_renderer_set_selection(headless, nullptr, 0u, -1);

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

    float camera_x = 0.0f;
    float camera_y = 0.0f;
    float camera_zoom = 0.0f;
    if (ee_renderer_set_editor_camera(headless, 12.5f, -8.25f, 64.0f) != EE_OK ||
        ee_renderer_get_editor_camera(headless, &camera_x, &camera_y, &camera_zoom) != EE_OK ||
        std::abs(camera_x - 12.5f) > epsilon ||
        std::abs(camera_y + 8.25f) > epsilon ||
        std::abs(camera_zoom - 64.0f) > epsilon)
    {
        std::cerr << "Editor camera state did not round-trip through the additive ABI.\n";
        destroy_headless();
        return 1;
    }
    if (ee_renderer_set_editor_camera(headless, 0.0f, 0.0f, 0.0f) == EE_OK)
    {
        std::cerr << "Editor camera accepted an invalid zoom.\n";
        destroy_headless();
        return 1;
    }

    const std::filesystem::path invalid_decoration =
        std::filesystem::temp_directory_path() / "extreme-editor-invalid-decoration.png";
    {
        std::ofstream output(invalid_decoration, std::ios::binary | std::ios::trunc);
        output << "not a png";
    }
    const EeResult invalid_decoration_result = ee_renderer_set_decoration_asset(
        headless,
        0xf00du,
        invalid_decoration.c_str());
    std::error_code remove_error;
    std::filesystem::remove(invalid_decoration, remove_error);
    if (invalid_decoration_result == EE_OK)
    {
        std::cerr << "Invalid decoration image was accepted as a loaded asset.\n";
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

    // Keep the scene still so comparisons at different chart times isolate only
    // SetFilter. Track glow remains pinned to the same visual_time below.
    if (ee_renderer_set_playback_timeline(headless, nullptr, 0u) != EE_OK)
    {
        std::cerr << "Could not clear playback before screen-filter pixel tests.\n";
        destroy_headless();
        return 1;
    }

    const auto render_filter_frame = [&](double time, std::vector<std::uint8_t>& output)
    {
        return ee_renderer_render_rgb(
            headless, time, 0.25, output.data(), frame_bytes, row_stride) == EE_OK;
    };
    const auto require_pixels_near = [&](const std::vector<std::uint8_t>& actual,
                                         const std::vector<std::uint8_t>& source,
                                         float grayscale,
                                         bool invert,
                                         const char* message)
    {
        for (std::size_t i = 0; i + 2u < actual.size(); i += 3u)
        {
            const float r = source[i];
            const float g = source[i + 1u];
            const float b = source[i + 2u];
            // CameraFilterPackLegacy/Color_GrayScale weights, decoded from the stock shader.
            const float luma = 0.222f * r + 0.707f * g + 0.071f * b;
            const float expected[3] =
            {
                r + (luma - r) * grayscale,
                g + (luma - g) * grayscale,
                b + (luma - b) * grayscale
            };
            for (std::size_t channel = 0; channel < 3u; ++channel)
            {
                const float filtered = invert ? 255.0f - expected[channel] : expected[channel];
                if (std::abs(static_cast<float>(actual[i + channel]) - filtered) > 2.0f)
                {
                    std::cerr << message << " at byte " << i + channel
                              << ": expected " << filtered
                              << ", actual " << static_cast<unsigned>(actual[i + channel]) << ".\n";
                    return false;
                }
            }
        }
        return true;
    };

    // SetFilter must be a final-frame operation: these checks exercise the real
    // offscreen D2D/D3D render target and RGB readback, not only matrix helpers.
    std::vector<std::uint8_t> filter_baseline(frame_bytes);
    std::vector<std::uint8_t> filter_frame(frame_bytes);
    if (ee_renderer_set_filter_timeline(headless, nullptr, 0u) != EE_OK ||
        !render_filter_frame(0.25, filter_baseline))
    {
        std::cerr << "Headless filter baseline render failed.\n";
        destroy_headless();
        return 1;
    }

    EeFilterEvent grayscale{};
    grayscale.start_time = 1.0;
    grayscale.duration_seconds = 2.0;
    grayscale.filter = 0u;
    grayscale.target_intensity = 1.0f;
    grayscale.enabled = 1u;
    if (ee_renderer_set_filter_timeline(headless, &grayscale, 1u) != EE_OK ||
        !render_filter_frame(0.5, filter_frame) || filter_frame != filter_baseline ||
        !render_filter_frame(1.0, filter_frame) || filter_frame != filter_baseline ||
        !render_filter_frame(2.0, filter_frame) ||
        !require_pixels_near(filter_frame, filter_baseline, 0.5f, false,
            "Grayscale 50% pixel mismatch") ||
        !render_filter_frame(3.0, filter_frame) ||
        !require_pixels_near(filter_frame, filter_baseline, 1.0f, false,
            "Grayscale 100% pixel mismatch"))
    {
        std::cerr << "Headless Grayscale timeline did not affect final pixels correctly.\n";
        destroy_headless();
        return 1;
    }

    // Render out of order to prove seeking reconstructs filter state.
    if (!render_filter_frame(1.0, filter_frame) || filter_frame != filter_baseline)
    {
        std::cerr << "Seeking backward did not reconstruct Grayscale 0%.\n";
        destroy_headless();
        return 1;
    }

    EeFilterEvent overlap[2]{};
    overlap[0].start_time = 0.0;
    overlap[0].filter = 0u;
    overlap[0].target_intensity = 1.0f;
    overlap[0].enabled = 1u;
    overlap[1].start_time = 0.0;
    overlap[1].filter = 2u;
    overlap[1].enabled = 1u;
    overlap[1].source_index = 1u;
    if (ee_renderer_set_filter_timeline(headless, overlap, 2u) != EE_OK ||
        !render_filter_frame(0.25, filter_frame) ||
        !require_pixels_near(filter_frame, filter_baseline, 1.0f, true,
            "Combined Grayscale and Invert pixel mismatch"))
    {
        std::cerr << "Overlapping screen filters did not compose correctly.\n";
        destroy_headless();
        return 1;
    }

    overlap[1].flags = EE_FILTER_DISABLE_OTHERS;
    if (ee_renderer_set_filter_timeline(headless, overlap, 2u) != EE_OK ||
        !render_filter_frame(0.25, filter_frame) ||
        !require_pixels_near(filter_frame, filter_baseline, 0.0f, true,
            "disableOthers Invert pixel mismatch"))
    {
        std::cerr << "SetFilter disableOthers did not disable Grayscale.\n";
        destroy_headless();
        return 1;
    }

    overlap[1].enabled = 0u;
    overlap[1].flags = 0u;
    if (ee_renderer_set_filter_timeline(headless, overlap + 1u, 1u) != EE_OK ||
        !render_filter_frame(0.25, filter_frame) || filter_frame != filter_baseline)
    {
        std::cerr << "Disabled Invert changed headless pixels.\n";
        destroy_headless();
        return 1;
    }

    EeFlashEvent foreground_flash{};
    foreground_flash.start_color = 0x204060u;
    foreground_flash.end_color = 0x204060u;
    foreground_flash.start_opacity = 0.45f;
    foreground_flash.end_opacity = 0.45f;
    foreground_flash.plane = EE_FLASH_PLANE_FOREGROUND;
    if (ee_renderer_set_flash_timeline(headless, &foreground_flash, 1u) != EE_OK ||
        ee_renderer_set_filter_timeline(headless, nullptr, 0u) != EE_OK ||
        !render_filter_frame(0.25, filter_baseline))
    {
        std::cerr << "Foreground Flash baseline for screen filters failed.\n";
        destroy_headless();
        return 1;
    }
    EeFilterEvent invert{};
    invert.filter = ee::FilterInvert;
    invert.enabled = 1u;
    if (ee_renderer_set_filter_timeline(headless, &invert, 1u) != EE_OK ||
        !render_filter_frame(0.25, filter_frame) ||
        !require_pixels_near(filter_frame, filter_baseline, 0.0f, true,
            "Foreground Flash and Invert pixel mismatch"))
    {
        std::cerr << "Screen filter was not applied after foreground Flash.\n";
        destroy_headless();
        return 1;
    }

    // A saturated colour separates the stock grayscale weights from other
    // luma conventions: pure green becomes 0.707 * 255.
    EeFlashEvent green_flash{};
    green_flash.start_color = 0x00ff00u;
    green_flash.end_color = 0x00ff00u;
    green_flash.start_opacity = 1.0f;
    green_flash.end_opacity = 1.0f;
    green_flash.plane = EE_FLASH_PLANE_FOREGROUND;
    EeFilterEvent full_grayscale{};
    full_grayscale.filter = ee::FilterGrayscale;
    full_grayscale.target_intensity = 1.0f;
    full_grayscale.enabled = 1u;
    if (ee_renderer_set_flash_timeline(headless, &green_flash, 1u) != EE_OK ||
        ee_renderer_set_filter_timeline(headless, &full_grayscale, 1u) != EE_OK ||
        !render_filter_frame(0.25, filter_frame))
    {
        std::cerr << "Green Flash grayscale render failed.\n";
        destroy_headless();
        return 1;
    }
    for (std::size_t i = 0; i < filter_frame.size(); ++i)
    {
        if (std::abs(static_cast<float>(filter_frame[i]) - 0.707f * 255.0f) > 2.0f)
        {
            std::cerr << "Grayscale of pure green is " << static_cast<unsigned>(filter_frame[i])
                      << ", expected stock weight 0.707.\n";
            destroy_headless();
            return 1;
        }
    }

    // Sepia and Contrast follow the stock CameraFilterPackLegacy shaders:
    //   sepia:    c' = c + I * (dot(c, w) - c) + I * (0.437, 0.171, 0.078)
    //   contrast: c' = (c - 0.5) * C + 0.5, C = intensity + 1
    // with w = (0.222, 0.707, 0.071); output is clamped by the 8-bit target.
    const auto require_pixels_mapped = [&](const std::vector<std::uint8_t>& actual,
                                           const std::vector<std::uint8_t>& source,
                                           auto&& map,
                                           const char* message)
    {
        for (std::size_t i = 0; i + 2u < actual.size(); i += 3u)
        {
            float rgb[3] = {source[i] / 255.0f, source[i + 1u] / 255.0f, source[i + 2u] / 255.0f};
            map(rgb);
            for (std::size_t channel = 0; channel < 3u; ++channel)
            {
                const float expected = std::clamp(rgb[channel], 0.0f, 1.0f) * 255.0f;
                if (std::abs(static_cast<float>(actual[i + channel]) - expected) > 2.0f)
                {
                    std::cerr << message << " at byte " << i + channel
                              << ": expected " << expected
                              << ", actual " << static_cast<unsigned>(actual[i + channel]) << ".\n";
                    return false;
                }
            }
        }
        return true;
    };
    const auto sepia = [](float intensity)
    {
        return [intensity](float* c)
        {
            const float luma = 0.222f * c[0] + 0.707f * c[1] + 0.071f * c[2];
            const float tint[3] = {0.437f, 0.171f, 0.078f};
            for (int k = 0; k < 3; ++k)
                c[k] = c[k] + intensity * (luma - c[k]) + intensity * tint[k];
        };
    };
    const auto contrast = [](float intensity)
    {
        return [intensity](float* c)
        {
            for (int k = 0; k < 3; ++k)
                c[k] = (c[k] - 0.5f) * (intensity + 1.0f) + 0.5f;
        };
    };

    if (ee_renderer_set_flash_timeline(headless, nullptr, 0u) != EE_OK ||
        ee_renderer_set_filter_timeline(headless, nullptr, 0u) != EE_OK ||
        !render_filter_frame(0.25, filter_baseline))
    {
        std::cerr << "Sepia/Contrast baseline render failed.\n";
        destroy_headless();
        return 1;
    }

    EeFilterEvent sepia_event{};
    sepia_event.start_time = 1.0;
    sepia_event.duration_seconds = 2.0;
    sepia_event.filter = 1u;
    sepia_event.target_intensity = 1.0f;
    sepia_event.enabled = 1u;
    if (ee_renderer_set_filter_timeline(headless, &sepia_event, 1u) != EE_OK ||
        !render_filter_frame(0.5, filter_frame) || filter_frame != filter_baseline ||
        !render_filter_frame(2.0, filter_frame) ||
        !require_pixels_mapped(filter_frame, filter_baseline, sepia(0.5f), "Sepia 50% pixel mismatch") ||
        !render_filter_frame(3.0, filter_frame) ||
        !require_pixels_mapped(filter_frame, filter_baseline, sepia(1.0f), "Sepia 100% pixel mismatch") ||
        !render_filter_frame(0.5, filter_frame) || filter_frame != filter_baseline)
    {
        std::cerr << "Headless Sepia timeline did not affect final pixels correctly.\n";
        destroy_headless();
        return 1;
    }

    EeFilterEvent contrast_event{};
    contrast_event.filter = 33u;
    contrast_event.target_intensity = 0.5f;
    contrast_event.enabled = 1u;
    if (ee_renderer_set_filter_timeline(headless, &contrast_event, 1u) != EE_OK ||
        !render_filter_frame(0.25, filter_frame) ||
        !require_pixels_mapped(filter_frame, filter_baseline, contrast(0.5f), "Contrast 150% pixel mismatch"))
    {
        std::cerr << "Headless Contrast did not affect final pixels correctly.\n";
        destroy_headless();
        return 1;
    }
    contrast_event.target_intensity = 0.0f;
    if (ee_renderer_set_filter_timeline(headless, &contrast_event, 1u) != EE_OK ||
        !render_filter_frame(0.25, filter_frame) ||
        !require_pixels_mapped(filter_frame, filter_baseline, contrast(0.0f), "Contrast 100% pixel mismatch"))
    {
        std::cerr << "Contrast at intensity 0 changed pixels.\n";
        destroy_headless();
        return 1;
    }

    // Composition follows ascending Filter id: Sepia (1) before Contrast (33).
    EeFilterEvent sepia_contrast[2]{};
    sepia_contrast[0].filter = 1u;
    sepia_contrast[0].target_intensity = 0.5f;
    sepia_contrast[0].enabled = 1u;
    sepia_contrast[1].filter = 33u;
    sepia_contrast[1].target_intensity = 0.25f;
    sepia_contrast[1].enabled = 1u;
    sepia_contrast[1].source_index = 1;
    if (ee_renderer_set_filter_timeline(headless, sepia_contrast, 2u) != EE_OK ||
        !render_filter_frame(0.25, filter_frame) ||
        !require_pixels_mapped(filter_frame, filter_baseline,
            [&](float* c) { sepia(0.5f)(c); contrast(0.25f)(c); },
            "Sepia then Contrast pixel mismatch"))
    {
        std::cerr << "Sepia and Contrast did not compose in Filter id order.\n";
        destroy_headless();
        return 1;
    }
    sepia_contrast[1].flags = EE_FILTER_DISABLE_OTHERS;
    if (ee_renderer_set_filter_timeline(headless, sepia_contrast, 2u) != EE_OK ||
        !render_filter_frame(0.25, filter_frame) ||
        !require_pixels_mapped(filter_frame, filter_baseline, contrast(0.25f),
            "disableOthers Contrast pixel mismatch"))
    {
        std::cerr << "Contrast disableOthers did not disable Sepia.\n";
        destroy_headless();
        return 1;
    }

    destroy_headless();
    std::cout << "PASS: native renderer ABI + deterministic headless RGB smoke test.\n";
    return 0;
}
