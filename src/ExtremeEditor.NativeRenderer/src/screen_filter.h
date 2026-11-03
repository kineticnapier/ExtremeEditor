#pragma once

#include "extreme_editor_renderer.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>

// SetFilter state and screen-space colour filters.
//
// Stock ADOFAI (ffxSetFilterPlus) keeps one camera filter component per
// Filter value. An event kills that filter's running tween with completion,
// then either sets the intensity at once (duration 0) or tweens it from the
// current value; a filter that was disabled starts from its default value.
// disableOthers completes and disables every other filter.
namespace ee
{
inline constexpr std::uint32_t FilterCount = 41u;
inline constexpr std::uint32_t FilterGrayscale = 0u;
inline constexpr std::uint32_t FilterInvert = 2u;

struct FilterVisualEntry
{
    bool enabled = false;
    float intensity = 0.0f;
    float applied_value = 0.0f;
};

struct FilterVisualState
{
    FilterVisualEntry entries[FilterCount]{};
};

// scrVfxPlus.filterDefaultValues; every other filter defaults to 0.
inline float FilterDefaultIntensity(std::uint32_t filter) noexcept
{
    switch (filter)
    {
    case 7u: return 1.0f;
    case 9u: return 1.0f;
    case 13u: return 0.01f;
    case 18u: return 0.5f;
    case 19u: return 0.5f;
    default: return 0.0f;
    }
}

using FilterEaseFunction = double (*)(std::uint32_t ease, double progress);

// Evaluates the SetFilter timeline at chart_time. events are ordered by
// start time. applied_value is left to the caller, which maps intensity to
// each filter's own parameter.
inline FilterVisualState ResolveScreenFilters(
    const EeFilterEvent* events,
    std::size_t event_count,
    double chart_time,
    FilterEaseFunction ease) noexcept
{
    struct Tween
    {
        bool active = false;
        bool enable = false;
        float from = 0.0f;
        float to = 0.0f;
        double start = 0.0;
        double duration = 0.0;
        std::uint32_t ease = 0u;
    };

    FilterVisualState result;
    Tween tweens[FilterCount]{};
    for (std::uint32_t i = 0u; i < FilterCount; ++i)
        result.entries[i].intensity = FilterDefaultIntensity(i);

    // Kill(complete: true) jumps a running tween to its end and applies it.
    const auto complete = [&](std::uint32_t filter) noexcept
    {
        Tween& tween = tweens[filter];
        if (!tween.active)
            return;
        result.entries[filter].intensity = tween.to;
        result.entries[filter].enabled = tween.enable;
        tween.active = false;
    };

    for (std::size_t index = 0; index < event_count; ++index)
    {
        const EeFilterEvent& item = events[index];
        if (item.start_time > chart_time)
            break;
        if (item.filter >= FilterCount)
            continue;

        const std::uint32_t filter = item.filter;
        const bool enable = item.enabled != 0u;
        complete(filter);
        FilterVisualEntry& entry = result.entries[filter];
        if (!(item.duration_seconds > 1e-9))
        {
            entry.intensity = item.target_intensity;
            entry.enabled = enable;
        }
        else
        {
            if (!entry.enabled)
                entry.intensity = FilterDefaultIntensity(filter);
            Tween& tween = tweens[filter];
            tween = Tween{true, enable, entry.intensity, item.target_intensity,
                item.start_time, item.duration_seconds, item.ease};
            // The tween setter calls SetFilter(filter, enable, value) from
            // its first update, so the enable state applies at once.
            entry.enabled = enable;
        }

        if ((item.flags & EE_FILTER_DISABLE_OTHERS) != 0u)
        {
            for (std::uint32_t other = 0u; other < FilterCount; ++other)
            {
                if (other == filter)
                    continue;
                complete(other);
                result.entries[other].enabled = false;
            }
        }
    }

    for (std::uint32_t filter = 0u; filter < FilterCount; ++filter)
    {
        const Tween& tween = tweens[filter];
        if (!tween.active)
            continue;
        const double raw = std::clamp((chart_time - tween.start) / tween.duration, 0.0, 1.0);
        const double progress = ease != nullptr ? ease(tween.ease, raw) : raw;
        result.entries[filter].intensity = static_cast<float>(
            tween.from + (tween.to - tween.from) * progress);
    }
    return result;
}

// Row-major 5x4 colour matrix in the layout of D2D1_MATRIX_5X4_F: rows are
// the R, G, B, A inputs and the constant offset; columns are the outputs.
struct ScreenColorMatrix
{
    float m[5][4]{};

    static ScreenColorMatrix Identity() noexcept
    {
        ScreenColorMatrix result;
        for (int i = 0; i < 4; ++i)
            result.m[i][i] = 1.0f;
        return result;
    }
};

// Applies `next` after `first`.
inline ScreenColorMatrix ComposeColorMatrices(
    const ScreenColorMatrix& first,
    const ScreenColorMatrix& next) noexcept
{
    ScreenColorMatrix result;
    for (int column = 0; column < 4; ++column)
    {
        for (int row = 0; row < 5; ++row)
        {
            float value = row == 4 ? next.m[4][column] : 0.0f;
            for (int k = 0; k < 4; ++k)
                value += first.m[row][k] * next.m[k][column];
            result.m[row][column] = value;
        }
    }
    return result;
}

// Rec. 601 luma weights. The stock grayscale shader is compiled into the
// game's resources and was not decoded, so the exact weights are unverified.
inline constexpr float GrayscaleWeightR = 0.299f;
inline constexpr float GrayscaleWeightG = 0.587f;
inline constexpr float GrayscaleWeightB = 0.114f;

// Combines the enabled filters that are a per-pixel colour transform into
// one matrix. Returns false when none is enabled, so the frame is left
// untouched. Filters that sample neighbouring pixels need their own passes
// and are not handled here.
inline bool BuildScreenFilterColorMatrix(
    const FilterVisualState& state,
    ScreenColorMatrix& output) noexcept
{
    bool any = false;
    ScreenColorMatrix matrix = ScreenColorMatrix::Identity();

    const FilterVisualEntry& grayscale = state.entries[FilterGrayscale];
    if (grayscale.enabled)
    {
        const float amount = std::clamp(grayscale.intensity, 0.0f, 1.0f);
        ScreenColorMatrix gray = ScreenColorMatrix::Identity();
        const float weights[3] = {GrayscaleWeightR, GrayscaleWeightG, GrayscaleWeightB};
        for (int input = 0; input < 3; ++input)
        {
            for (int out = 0; out < 3; ++out)
            {
                const float identity = input == out ? 1.0f : 0.0f;
                gray.m[input][out] = identity + (weights[input] - identity) * amount;
            }
        }
        matrix = ComposeColorMatrices(matrix, gray);
        any = true;
    }

    // Stock SetFilter has no intensity parameter for Invert: enabling it
    // inverts fully whatever the intensity.
    if (state.entries[FilterInvert].enabled)
    {
        ScreenColorMatrix invert = ScreenColorMatrix::Identity();
        for (int channel = 0; channel < 3; ++channel)
        {
            invert.m[channel][channel] = -1.0f;
            invert.m[4][channel] = 1.0f;
        }
        matrix = ComposeColorMatrices(matrix, invert);
        any = true;
    }

    if (any)
        output = matrix;
    return any;
}
}
