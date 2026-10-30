#pragma once

#include <cstdint>
#include <limits>

namespace ee
{
// Win32 CreateWindowExW/MoveWindow take signed int dimensions, while the
// renderer's public API accepts uint32_t. Preserve zero and ordinary sizes,
// but never wrap a large unsigned extent to a negative Win32 dimension.
[[nodiscard]] constexpr int ToWin32WindowExtent(std::uint32_t extent) noexcept
{
    constexpr std::uint32_t max_extent =
        static_cast<std::uint32_t>(std::numeric_limits<int>::max());
    return static_cast<int>(extent > max_extent ? max_extent : extent);
}
}
