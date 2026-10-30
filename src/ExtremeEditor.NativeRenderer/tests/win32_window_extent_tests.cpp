#include "win32_window_extent.h"

#include <cstdint>
#include <limits>

static_assert(ee::ToWin32WindowExtent(0u) == 0);
static_assert(ee::ToWin32WindowExtent(1u) == 1);
static_assert(ee::ToWin32WindowExtent(800u) == 800);
static_assert(ee::ToWin32WindowExtent(
    static_cast<std::uint32_t>(std::numeric_limits<int>::max())) ==
    std::numeric_limits<int>::max());
static_assert(ee::ToWin32WindowExtent(
    static_cast<std::uint32_t>(std::numeric_limits<int>::max()) + 1u) ==
    std::numeric_limits<int>::max());
static_assert(ee::ToWin32WindowExtent(
    std::numeric_limits<std::uint32_t>::max()) ==
    std::numeric_limits<int>::max());

int main()
{
    return 0;
}
