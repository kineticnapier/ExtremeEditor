#pragma once

#include <cstdint>
#include <limits>
#include <span>

namespace ee
{
struct FloorHitCandidate
{
    std::uint32_t floor = 0;
    float distance_squared = 0.0f;
    float draw_depth = 1.0f;
};

// Preserve nearest-center picking; only equal distances use the draw depth.
inline std::int32_t SelectNearestFloorHit(
    std::span<const FloorHitCandidate> candidates,
    float maximum_distance_squared) noexcept
{
    float best_distance_squared = maximum_distance_squared;
    std::int32_t best_floor = -1;
    float best_depth = std::numeric_limits<float>::infinity();
    for (const FloorHitCandidate& candidate : candidates)
    {
        if (candidate.distance_squared < best_distance_squared ||
            (candidate.distance_squared == best_distance_squared && candidate.draw_depth <= best_depth))
        {
            best_distance_squared = candidate.distance_squared;
            best_floor = static_cast<std::int32_t>(candidate.floor);
            best_depth = candidate.draw_depth;
        }
    }
    return best_floor;
}
}
