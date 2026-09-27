#pragma once

#include <cstdint>
#include <string>
#include <unordered_map>

namespace ee
{
struct DecorationAsset
{
    std::wstring image_path;
};

using DecorationAssetTable = std::unordered_map<std::uint32_t, DecorationAsset>;
}
