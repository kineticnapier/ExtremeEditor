#include "level_scene.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace ee
{
std::shared_ptr<LevelScene> LevelScene::Create(
    const EeFloor* floor_data,
    std::uint32_t floor_count,
    const EeGeometry* geometry_data,
    std::uint32_t geometry_count,
    const EePoint* point_data,
    std::uint32_t point_count,
    float bounds_left,
    float bounds_top,
    float bounds_right,
    float bounds_bottom)
{
    if (floor_count == 0 || floor_data == nullptr ||
        geometry_count == 0 || geometry_data == nullptr ||
        point_count == 0 || point_data == nullptr)
        return nullptr;

    auto scene = std::make_shared<LevelScene>();
    scene->floors.assign(floor_data, floor_data + floor_count);
    scene->geometries.assign(geometry_data, geometry_data + geometry_count);
    scene->points.assign(point_data, point_data + point_count);
    scene->bounds_left = bounds_left;
    scene->bounds_top = bounds_top;
    scene->bounds_right = bounds_right;
    scene->bounds_bottom = bounds_bottom;

    for (const EeGeometry& geometry : scene->geometries)
    {
        const std::uint64_t end = static_cast<std::uint64_t>(geometry.point_offset) + geometry.point_count;
        if (geometry.point_count < 3 || end > scene->points.size())
            return nullptr;
    }

    scene->cells.reserve(std::min<std::size_t>(scene->floors.size(), 262144u));
    for (std::uint32_t i = 0; i < floor_count; ++i)
    {
        const EeFloor& floor = scene->floors[i];
        if (floor.geometry_id >= geometry_count || !std::isfinite(floor.x) || !std::isfinite(floor.y))
            return nullptr;

        const int x = FastFloor(floor.x / CellSize);
        const int y = FastFloor(floor.y / CellSize);
        scene->cells[Key(x, y)].push_back(i);
    }

    return scene;
}

void LevelScene::Query(float left, float top, float right, float bottom, std::vector<std::uint32_t>& output) const
{
    output.clear();

    const int min_x = FastFloor(left / CellSize);
    const int max_x = FastFloor(right / CellSize);
    const int min_y = FastFloor(top / CellSize);
    const int max_y = FastFloor(bottom / CellSize);

    const std::int64_t cells_wide = static_cast<std::int64_t>(max_x) - min_x + 1;
    const std::int64_t cells_high = static_cast<std::int64_t>(max_y) - min_y + 1;
    if (cells_wide <= 0 || cells_high <= 0 || cells_wide * cells_high > 2000000)
        return;

    for (int y = min_y; y <= max_y; ++y)
    {
        for (int x = min_x; x <= max_x; ++x)
        {
            const auto found = cells.find(Key(x, y));
            if (found == cells.end())
                continue;

            output.insert(output.end(), found->second.begin(), found->second.end());
        }
    }
}

int LevelScene::FastFloor(float value) noexcept
{
    const int truncated = static_cast<int>(value);
    return value < static_cast<float>(truncated) ? truncated - 1 : truncated;
}

std::int64_t LevelScene::Key(int x, int y) noexcept
{
    return (static_cast<std::int64_t>(x) << 32) ^ static_cast<std::uint32_t>(y);
}
}
