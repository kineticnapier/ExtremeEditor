#pragma once

#include "level_scene.h"

#include <cmath>
#include <cstdint>
#include <vector>

namespace ee::detail
{
namespace triangulation
{
constexpr double Epsilon = 1e-7;

inline double Cross(const EePoint& a, const EePoint& b, const EePoint& c) noexcept
{
    return (static_cast<double>(b.x) - a.x) * (static_cast<double>(c.y) - a.y) -
           (static_cast<double>(b.y) - a.y) * (static_cast<double>(c.x) - a.x);
}

inline double DistanceSquared(const EePoint& a, const EePoint& b) noexcept
{
    const double dx = static_cast<double>(a.x) - b.x;
    const double dy = static_cast<double>(a.y) - b.y;
    return dx * dx + dy * dy;
}

inline bool PointInTriangle(
    const EePoint& point,
    const EePoint& a,
    const EePoint& b,
    const EePoint& c,
    double winding) noexcept
{
    return Cross(a, b, point) * winding >= -Epsilon &&
           Cross(b, c, point) * winding >= -Epsilon &&
           Cross(c, a, point) * winding >= -Epsilon;
}

inline bool TriangulateSimplePolygon(
    const EePoint* points,
    std::uint32_t point_count,
    std::vector<std::uint32_t>& output) noexcept
{
    output.clear();
    if (points == nullptr || point_count < 3)
        return false;

    std::vector<std::uint32_t> vertices;
    vertices.reserve(point_count);

    // D2D accepted the polygon as a path and tessellated it internally. Keep the
    // original indices, but discard duplicate consecutive points before doing the
    // same job explicitly for D3D11.
    for (std::uint32_t i = 0; i < point_count; ++i)
    {
        if (!vertices.empty() &&
            DistanceSquared(points[vertices.back()], points[i]) <= Epsilon * Epsilon)
        {
            continue;
        }
        vertices.push_back(i);
    }

    if (vertices.size() >= 2 &&
        DistanceSquared(points[vertices.front()], points[vertices.back()]) <= Epsilon * Epsilon)
    {
        vertices.pop_back();
    }

    if (vertices.size() < 3)
        return false;

    // Remove collinear vertices. They do not contribute to the fill and can make
    // an ear appear to contain another vertex exactly on its edge.
    bool removed = true;
    while (removed && vertices.size() > 3)
    {
        removed = false;
        for (std::size_t i = 0; i < vertices.size(); ++i)
        {
            const std::size_t prev = (i + vertices.size() - 1) % vertices.size();
            const std::size_t next = (i + 1) % vertices.size();
            if (std::abs(Cross(
                    points[vertices[prev]],
                    points[vertices[i]],
                    points[vertices[next]])) <= Epsilon)
            {
                vertices.erase(vertices.begin() + static_cast<std::ptrdiff_t>(i));
                removed = true;
                break;
            }
        }
    }

    if (vertices.size() < 3)
        return false;

    double signed_area_twice = 0.0;
    for (std::size_t i = 0; i < vertices.size(); ++i)
    {
        const EePoint& a = points[vertices[i]];
        const EePoint& b = points[vertices[(i + 1) % vertices.size()]];
        signed_area_twice += static_cast<double>(a.x) * b.y -
                             static_cast<double>(a.y) * b.x;
    }

    if (std::abs(signed_area_twice) <= Epsilon)
        return false;

    const double winding = signed_area_twice > 0.0 ? 1.0 : -1.0;
    const std::size_t triangle_count = vertices.size() - 2;
    output.reserve(triangle_count * 3);

    while (vertices.size() > 3)
    {
        bool clipped_ear = false;
        for (std::size_t i = 0; i < vertices.size(); ++i)
        {
            const std::size_t prev = (i + vertices.size() - 1) % vertices.size();
            const std::size_t next = (i + 1) % vertices.size();
            const std::uint32_t ia = vertices[prev];
            const std::uint32_t ib = vertices[i];
            const std::uint32_t ic = vertices[next];

            if (Cross(points[ia], points[ib], points[ic]) * winding <= Epsilon)
                continue;

            bool contains_vertex = false;
            for (std::size_t j = 0; j < vertices.size(); ++j)
            {
                if (j == prev || j == i || j == next)
                    continue;

                if (PointInTriangle(
                        points[vertices[j]],
                        points[ia],
                        points[ib],
                        points[ic],
                        winding))
                {
                    contains_vertex = true;
                    break;
                }
            }

            if (contains_vertex)
                continue;

            output.push_back(ia);
            output.push_back(ib);
            output.push_back(ic);
            vertices.erase(vertices.begin() + static_cast<std::ptrdiff_t>(i));
            clipped_ear = true;
            break;
        }

        if (!clipped_ear)
        {
            output.clear();
            return false;
        }
    }

    output.push_back(vertices[0]);
    output.push_back(vertices[1]);
    output.push_back(vertices[2]);
    return output.size() == triangle_count * 3;
}
}
} // namespace ee::detail
