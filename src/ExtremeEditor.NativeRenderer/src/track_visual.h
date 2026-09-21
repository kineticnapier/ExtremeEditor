#pragma once

#include "../include/extreme_editor_renderer.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>

namespace ee
{
struct ResolvedTrackVisual
{
    float r = 1.0f;
    float g = 1.0f;
    float b = 1.0f;
    // Integer part = style index, fractional part = glow strength.
    float style_glow = 0.0f;
};

inline float TrackVisualTimeSeconds() noexcept
{
    static const auto origin = std::chrono::steady_clock::now();
    return static_cast<float>(
        std::chrono::duration<double>(std::chrono::steady_clock::now() - origin).count());
}

inline float TrackFract(float value) noexcept
{
    return value - std::floor(value);
}

inline void UnpackTrackColor(
    std::uint32_t packed,
    float& r,
    float& g,
    float& b) noexcept
{
    r = static_cast<float>(packed & 0xffu) / 255.0f;
    g = static_cast<float>((packed >> 8) & 0xffu) / 255.0f;
    b = static_cast<float>((packed >> 16) & 0xffu) / 255.0f;
}

inline void HsvToRgb(float h, float s, float v, float& r, float& g, float& b) noexcept
{
    h = TrackFract(h) * 6.0f;
    const int sector = static_cast<int>(std::floor(h));
    const float f = h - static_cast<float>(sector);
    const float p = v * (1.0f - s);
    const float q = v * (1.0f - s * f);
    const float t = v * (1.0f - s * (1.0f - f));
    switch (sector % 6)
    {
    case 0: r = v; g = t; b = p; break;
    case 1: r = q; g = v; b = p; break;
    case 2: r = p; g = v; b = t; break;
    case 3: r = p; g = q; b = v; break;
    case 4: r = t; g = p; b = v; break;
    default: r = v; g = p; b = q; break;
    }
}

inline void RgbToHsv(float r, float g, float b, float& h, float& s, float& v) noexcept
{
    const float maximum = std::max(r, std::max(g, b));
    const float minimum = std::min(r, std::min(g, b));
    const float delta = maximum - minimum;
    v = maximum;
    s = maximum <= 0.000001f ? 0.0f : delta / maximum;
    if (delta <= 0.000001f)
    {
        h = 0.0f;
        return;
    }
    if (maximum == r)
        h = (g - b) / delta + (g < b ? 6.0f : 0.0f);
    else if (maximum == g)
        h = (b - r) / delta + 2.0f;
    else
        h = (r - g) / delta + 4.0f;
    h /= 6.0f;
}

inline ResolvedTrackVisual ResolveTrackVisual(
    const EeFloor& floor,
    std::uint32_t floor_index,
    float time_seconds) noexcept
{
    ResolvedTrackVisual result;
    if ((floor.track_visual_flags & EE_TRACK_VISUAL_ENABLED) == 0u)
        return result;

    float r1 = 1.0f;
    float g1 = 1.0f;
    float b1 = 1.0f;
    float r2 = 1.0f;
    float g2 = 1.0f;
    float b2 = 1.0f;
    UnpackTrackColor(floor.track_primary_color, r1, g1, b1);
    UnpackTrackColor(floor.track_secondary_color, r2, g2, b2);

    const std::uint32_t color_type = floor.track_visual_flags & EE_TRACK_VISUAL_COLOR_TYPE_MASK;
    const std::uint32_t style =
        (floor.track_visual_flags & EE_TRACK_VISUAL_STYLE_MASK) >> EE_TRACK_VISUAL_STYLE_SHIFT;
    const std::uint32_t pulse =
        (floor.track_visual_flags & EE_TRACK_VISUAL_PULSE_MASK) >> EE_TRACK_VISUAL_PULSE_SHIFT;

    const float duration = std::max(0.000001f, floor.track_anim_duration);
    float phase = TrackFract(time_seconds / duration);
    const float pulse_length = static_cast<float>(std::max<std::uint32_t>(1u, floor.track_pulse_length));
    const float tile_phase =
        (static_cast<float>(floor_index) - static_cast<float>(floor.track_start_floor)) / pulse_length;
    if (pulse == 1u) // Forward
        phase = TrackFract(phase - tile_phase);
    else if (pulse == 2u) // Backward
        phase = TrackFract(phase + tile_phase);

    float mix = 0.0f;
    switch (color_type)
    {
    case 1u: // Stripes
        mix = ((static_cast<std::int64_t>(floor_index) - floor.track_start_floor) & 1ll) != 0 ? 1.0f : 0.0f;
        break;
    case 2u: // Glow: primary -> secondary -> primary
        mix = 1.0f - std::abs(phase * 2.0f - 1.0f);
        break;
    case 3u: // Blink
        mix = phase;
        break;
    case 4u: // Switch
        mix = phase < 0.5f ? 0.0f : 1.0f;
        break;
    case 5u: // Rainbow
    {
        float h = 0.0f;
        float s = 0.0f;
        float v = 0.0f;
        RgbToHsv(r1, g1, b1, h, s, v);
        HsvToRgb(h + phase, s, v, result.r, result.g, result.b);
        result.style_glow = static_cast<float>(style) +
            std::clamp(floor.track_glow_intensity, 0.0f, 0.999f);
        return result;
    }
    case 6u: // Volume uses the primary tint; audio-driven modulation is separate.
    case 0u: // Single
    default:
        mix = 0.0f;
        break;
    }

    result.r = r1 + (r2 - r1) * mix;
    result.g = g1 + (g2 - g1) * mix;
    result.b = b1 + (b2 - b1) * mix;
    result.style_glow = static_cast<float>(style) +
        std::clamp(floor.track_glow_intensity, 0.0f, 0.999f);
    return result;
}
}
