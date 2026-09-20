#pragma once

#include "extreme_editor_renderer.h"

#include <cstdint>
#include <memory>
#include <unordered_map>
#include <vector>

namespace ee
{
struct LevelScene
{
    static std::shared_ptr<LevelScene> Create(
        const EeFloor* floors,
        std::uint32_t floor_count,
        const EeGeometry* geometries,
        std::uint32_t geometry_count,
        const EePoint* points,
        std::uint32_t point_count,
        float bounds_left,
        float bounds_top,
        float bounds_right,
        float bounds_bottom);

    void Query(float left, float top, float right, float bottom, std::vector<std::uint32_t>& output) const;

    std::vector<EeFloor> floors;
    std::vector<EeGeometry> geometries;
    std::vector<EePoint> points;
    std::unordered_map<std::int64_t, std::vector<std::uint32_t>> cells;

    float bounds_left = 0.0f;
    float bounds_top = 0.0f;
    float bounds_right = 0.0f;
    float bounds_bottom = 0.0f;

private:
    static constexpr float CellSize = 32.0f;

    static int FastFloor(float value) noexcept;
    static std::int64_t Key(int x, int y) noexcept;
};
}
