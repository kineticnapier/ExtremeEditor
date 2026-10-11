#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>

namespace ee
{
// D3D floor bodies use LESS_EQUAL. Negative animation sorting offsets move
// a tile backwards. Keep picking and both instanced draw paths on this rule.
inline float FloorDrawDepth(std::uint32_t floor, std::int32_t sorting_offset,
    std::size_t floor_count) noexcept
{
    return std::clamp((static_cast<float>(floor) + 1.0f - static_cast<float>(sorting_offset)) /
        static_cast<float>(floor_count + 1u), 0.0f, 1.0f);
}
}
