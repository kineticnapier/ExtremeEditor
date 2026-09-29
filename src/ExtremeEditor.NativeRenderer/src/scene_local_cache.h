#pragma once

#include <cstdint>

namespace ee
{
inline bool ShouldRefreshSceneLocalCache(
    std::uint64_t cached_scene_generation,
    std::uint64_t cached_local_version,
    std::uint64_t incoming_scene_generation,
    std::uint64_t incoming_local_version) noexcept
{
    return cached_scene_generation != incoming_scene_generation ||
           cached_local_version != incoming_local_version;
}
}
