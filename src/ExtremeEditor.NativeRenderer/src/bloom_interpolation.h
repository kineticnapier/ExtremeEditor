#pragma once

#include <algorithm>
#include <cmath>
#include <limits>

namespace ee
{
inline float InterpolateBloomFinite(float from, float to, double progress) noexcept
{
    if (!std::isfinite(from) || !std::isfinite(to))
        return 0.0f;
    if (!std::isfinite(progress))
        progress = 0.0;

    const double value = static_cast<double>(from) +
        (static_cast<double>(to) - from) * progress;
    const double limit = static_cast<double>(std::numeric_limits<float>::max());
    return static_cast<float>(std::clamp(value, -limit, limit));
}
}
