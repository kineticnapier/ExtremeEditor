#pragma once

#include "extreme_editor_renderer.h"

#include <memory>
#include <vector>

namespace ee
{
// The floors exactly as uploaded. LevelScene creates one per scene and every
// track runtime reads the same array; the live scene floors are separate and
// are what the runtimes write to.
using FloorBase = std::shared_ptr<const std::vector<EeFloor>>;

inline const std::vector<EeFloor>& FloorsOf(const FloorBase& base) noexcept
{
    static const std::vector<EeFloor> empty;
    return base ? *base : empty;
}
}
