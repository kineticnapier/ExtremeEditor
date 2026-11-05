#include "bloom_interpolation.h"

#include <cmath>
#include <iostream>
#include <limits>

int main()
{
    constexpr float large = 2e38f;
    const float midpoint = ee::InterpolateBloomFinite(-large, large, 0.5);
    if (!std::isfinite(midpoint) || std::abs(midpoint) > 1e30f)
    {
        std::cerr << "Opposing finite Bloom endpoints overflowed at midpoint.\n";
        return 1;
    }

    const float maximum = std::numeric_limits<float>::max();
    if (ee::InterpolateBloomFinite(-maximum, maximum, 1.2) != maximum ||
        ee::InterpolateBloomFinite(maximum, -maximum, 1.2) != -maximum)
    {
        std::cerr << "Bloom overshoot was not saturated to finite float range.\n";
        return 1;
    }

    if (ee::InterpolateBloomFinite(2.0f, 6.0f, 0.25) != 3.0f ||
        ee::InterpolateBloomFinite(2.0f, 6.0f, 1.0) != 6.0f ||
        ee::InterpolateBloomFinite(2.0f, 6.0f,
            std::numeric_limits<double>::quiet_NaN()) != 2.0f)
    {
        std::cerr << "Ordinary Bloom interpolation or invalid progress regressed.\n";
        return 1;
    }

    std::cout << "PASS: finite Bloom interpolation and overshoot.\n";
    return 0;
}
