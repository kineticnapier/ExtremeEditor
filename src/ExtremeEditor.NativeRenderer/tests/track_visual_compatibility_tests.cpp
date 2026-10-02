#include "track_visual.h"

#include <cmath>
#include <cstdint>
#include <iostream>
#include <string>
#include <vector>

namespace
{
constexpr float Epsilon = 0.0001f;

std::uint32_t Pack(std::uint8_t r, std::uint8_t g, std::uint8_t b) noexcept
{
    return static_cast<std::uint32_t>(r) |
        (static_cast<std::uint32_t>(g) << 8u) |
        (static_cast<std::uint32_t>(b) << 16u) |
        0xff000000u;
}

bool Near(float actual, float expected) noexcept
{
    return std::abs(actual - expected) <= Epsilon;
}

EeFloor MakeFloor(std::uint32_t color_type, std::uint32_t pulse = 0u) noexcept
{
    EeFloor floor{};
    floor.track_visual_flags = 0x80000000u | color_type | (pulse << 6u);
    floor.track_primary_color = Pack(0u, 0u, 0u);
    floor.track_secondary_color = Pack(255u, 255u, 255u);
    floor.track_anim_duration = 1.0f;
    floor.track_pulse_length = 4u;
    floor.track_start_floor = 3;
    return floor;
}

void Check(
    std::vector<std::string>& failures,
    const char* name,
    float actual,
    float expected)
{
    if (Near(actual, expected))
    {
        std::cout << "PASS: " << name << '\n';
        return;
    }

    std::cout << "RED: " << name << " expected=" << expected
              << " actual=" << actual << '\n';
    failures.emplace_back(name);
}
}

int main()
{
    std::vector<std::string> failures;

    // Headless rendering supplies visualTime explicitly. The override must be
    // deterministic, nest safely, and restore the previous frame clock.
    {
        ee::ScopedTrackVisualTime outer(0.25);
        Check(failures, "Explicit visual time", ee::TrackVisualTimeSeconds(), 0.25f);

        {
            ee::ScopedTrackVisualTime inner(0.75);
            Check(failures, "Nested visual time", ee::TrackVisualTimeSeconds(), 0.75f);
        }

        Check(failures, "Visual time restore", ee::TrackVisualTimeSeconds(), 0.25f);

        EeFloor deterministic_blink = MakeFloor(3u);
        Check(failures, "Explicit visual time drives track loop",
            ee::ResolveTrackVisual(
                deterministic_blink,
                0u,
                ee::TrackVisualTimeSeconds()).r,
            0.25f);
    }

    // Glow uses DOTween's cosine-shaped loop, not a triangle wave.
    EeFloor glow = MakeFloor(2u);
    ee::ResolvedTrackVisual glow_result = ee::ResolveTrackVisual(glow, 0u, 0.125f);
    Check(failures, "Glow cosine phase", glow_result.r,
        (1.0f - std::cos(2.0f * 3.14159265358979323846f * 0.125f)) * 0.5f);

    // These already match stock and protect the GREEN change from collateral drift.
    EeFloor blink = MakeFloor(3u);
    Check(failures, "Blink sawtooth", ee::ResolveTrackVisual(blink, 0u, 0.25f).r, 0.25f);

    EeFloor switched = MakeFloor(4u);
    Check(failures, "Switch first half", ee::ResolveTrackVisual(switched, 0u, 0.25f).r, 0.0f);
    Check(failures, "Switch second half", ee::ResolveTrackVisual(switched, 0u, 0.75f).r, 1.0f);

    // Stock Rainbow uses phase as the absolute hue and preserves only S/V.
    EeFloor rainbow = MakeFloor(5u);
    rainbow.track_primary_color = Pack(0u, 255u, 0u);
    ee::ResolvedTrackVisual rainbow_result = ee::ResolveTrackVisual(rainbow, 0u, 0.25f);
    Check(failures, "Rainbow absolute hue red", rainbow_result.r, 0.5f);
    Check(failures, "Rainbow absolute hue green", rainbow_result.g, 1.0f);
    Check(failures, "Rainbow absolute hue blue", rainbow_result.b, 0.0f);

    // Pulse offset is based on absolute seqID modulo pulseLength. startFloor is
    // Recolor metadata and must not rebase the periodic pulse.
    EeFloor forward = MakeFloor(3u, 1u);
    Check(failures, "Forward pulse absolute seqID",
        ee::ResolveTrackVisual(forward, 5u, 0.125f).r,
        0.875f);

    EeFloor backward = MakeFloor(3u, 2u);
    Check(failures, "Backward pulse absolute seqID",
        ee::ResolveTrackVisual(backward, 5u, 0.125f).r,
        0.375f);

    EeFloor no_pulse = MakeFloor(3u, 0u);
    Check(failures, "Pulse None has no tile offset",
        ee::ResolveTrackVisual(no_pulse, 5u, 0.125f).r,
        0.125f);

    if (!failures.empty())
    {
        std::cerr << "FAIL: track visual compatibility regressions=" << failures.size() << '\n';
        return 1;
    }
    return 0;
}
