#pragma once

#include <cstdint>
#include <algorithm>
#include <vector>
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
 
// The foremost visible floor wins. Repeated clicks at the same screen point
// advance through overlapping floors, wrapping from back to front.
inline std::int32_t SelectFrontmostFloorHit(
    std::span<const FloorHitCandidate> candidates,
    float maximum_distance_squared,
    std::int32_t previous_floor,
    bool repeated_click)
{
    std::vector<FloorHitCandidate> covered;
    for (const auto& candidate : candidates)
        if (candidate.distance_squared <= maximum_distance_squared)
            covered.push_back(candidate);
    std::sort(covered.begin(), covered.end(), [](const auto& a, const auto& b) {
        if (a.draw_depth != b.draw_depth)
            return a.draw_depth < b.draw_depth;
        return a.floor > b.floor;
    });
    if (covered.empty())
        return -1;
    if (repeated_click)
        for (std::size_t i = 0; i < covered.size(); ++i)
            if (static_cast<std::int32_t>(covered[i].floor) == previous_floor)
                return static_cast<std::int32_t>(covered[(i + 1) % covered.size()].floor);
    return static_cast<std::int32_t>(covered.front().floor);
}

}
