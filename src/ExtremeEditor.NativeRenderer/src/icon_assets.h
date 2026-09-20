#pragma once

#include <cstdint>
#include <string>
#include <unordered_map>

namespace ee
{
struct IconAsset
{
    std::wstring image_path;
    std::wstring outline_path;
};

using IconAssetTable = std::unordered_map<std::uint32_t, IconAsset>;
}
