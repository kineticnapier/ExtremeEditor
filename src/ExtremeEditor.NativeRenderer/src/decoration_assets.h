#pragma once

#include <cstdint>
#include <unordered_map>
#include <vector>

namespace ee
{
struct DecorationAsset
{
    std::uint32_t width = 0;
    std::uint32_t height = 0;
    std::uint32_t stride = 0;
    std::vector<std::uint8_t> pixels;
};

using DecorationAssetTable = std::unordered_map<std::uint32_t, DecorationAsset>;
}
