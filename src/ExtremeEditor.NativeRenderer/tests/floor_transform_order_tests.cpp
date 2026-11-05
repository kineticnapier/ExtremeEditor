#include "floor_transform.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <iostream>

namespace
{
constexpr float Pi = 3.14159265358979323846f;
constexpr float HalfLength = 0.75f;
constexpr float HalfWidth = 0.4125f;
constexpr float Epsilon = 0.0001f;

struct Bounds
{
    float width;
    float height;
};

bool Near(float actual, float expected, float epsilon = Epsilon)
{
    return std::abs(actual - expected) <= epsilon;
}

std::array<ee::FloorTransformPoint, 4> TransformRectangle(
    float original_entry,
    float scale_x,
    float scale_y,
    float rotation)
{
    const ee::FloorLocalTransform transform = ee::ResolveFloorLocalTransform(
        original_entry + rotation, rotation, scale_x, scale_y, true);
    return {
        ee::ApplyFloorLocalTransform(transform, -HalfLength, -HalfWidth),
        ee::ApplyFloorLocalTransform(transform, HalfLength, -HalfWidth),
        ee::ApplyFloorLocalTransform(transform, HalfLength, HalfWidth),
        ee::ApplyFloorLocalTransform(transform, -HalfLength, HalfWidth)};
}

Bounds Measure(const std::array<ee::FloorTransformPoint, 4>& points)
{
    float min_x = points[0].x;
    float max_x = points[0].x;
    float min_y = points[0].y;
    float max_y = points[0].y;
    for (const auto& point : points)
    {
        min_x = std::min(min_x, point.x);
        max_x = std::max(max_x, point.x);
        min_y = std::min(min_y, point.y);
        max_y = std::max(max_y, point.y);
    }
    return {max_x - min_x, max_y - min_y};
}

ee::FloorTransformPoint ApplyLegacy(
    float entry,
    float scale_x,
    float scale_y,
    float x,
    float y)
{
    const float c = std::cos(entry);
    const float s = std::sin(entry);
    x *= scale_x;
    y *= scale_y;
    return {c * x - s * y, s * x + c * y};
}
}

int main()
{
    struct Case
    {
        float entry;
        float scale_x;
        float scale_y;
        float rotation;
        float width;
        float height;
    };
    const Case cases[] =
    {
        {Pi * 0.5f, 3.0f, 1.0f, 0.0f, 2.475f, 1.5f},
        {Pi * 0.5f, 3.0f, 1.0f, Pi * 0.25f, 2.810749f, 2.810749f},
        {Pi * 0.5f, 1.0f, 2.0f, 0.0f, 0.825f, 3.0f},
        {Pi * 0.5f, 0.0f, 2.0f, 0.0f, 0.0f, 3.0f},
        {Pi * 0.25f, 0.0f, 2.0f, Pi * 0.25f, 2.325f, 2.325f}
    };

    for (const Case& item : cases)
    {
        const Bounds bounds = Measure(TransformRectangle(
            item.entry, item.scale_x, item.scale_y, item.rotation));
        if (!Near(bounds.width, item.width, 0.001f) ||
            !Near(bounds.height, item.height, 0.001f))
        {
            std::cerr << "floor transform bounds mismatch: expected "
                      << item.width << 'x' << item.height << ", actual "
                      << bounds.width << 'x' << bounds.height << '\n';
            return 1;
        }
    }

    const auto vertical = TransformRectangle(Pi * 0.5f, 3.0f, 1.0f, 0.0f);
    const ee::FloorTransformPoint expected[] =
    {
        {1.2375f, -0.75f},
        {1.2375f, 0.75f},
        {-1.2375f, 0.75f},
        {-1.2375f, -0.75f}
    };
    for (std::size_t i = 0; i < vertical.size(); ++i)
    {
        if (!Near(vertical[i].x, expected[i].x) ||
            !Near(vertical[i].y, expected[i].y))
        {
            std::cerr << "floor transform vertex " << i << " mismatch\n";
            return 1;
        }
    }

    const float sample_x = 0.63f;
    const float sample_y = -0.27f;
    for (float original_entry : {0.0f, Pi, Pi * 0.37f})
    {
        const float rotation = Pi * 0.23f;
        const float scale_x = original_entry == Pi * 0.37f ? 1.75f : 2.25f;
        const float scale_y = original_entry == Pi * 0.37f ? 1.75f : 0.65f;
        const float total_entry = original_entry + rotation;
        const auto transform = ee::ResolveFloorLocalTransform(
            total_entry, rotation, scale_x, scale_y, true);
        const auto actual = ee::ApplyFloorLocalTransform(transform, sample_x, sample_y);
        const auto legacy = ApplyLegacy(total_entry, scale_x, scale_y, sample_x, sample_y);
        if (!Near(actual.x, legacy.x) || !Near(actual.y, legacy.y))
        {
            std::cerr << "isotropic or axial-entry invariant regressed\n";
            return 1;
        }
    }

    const auto origin_transform = ee::ResolveFloorLocalTransform(
        Pi * 0.91f, Pi * 0.41f, 3.0f, 0.25f, true);
    const auto origin = ee::ApplyFloorLocalTransform(origin_transform, 0.0f, 0.0f);
    if (!Near(origin.x, 0.0f) || !Near(origin.y, 0.0f))
    {
        std::cerr << "floor transform origin moved\n";
        return 1;
    }

    // The selection outline consumes this same screen-space matrix. Verify it
    // is exactly the floor world transform followed by the camera transform.
    const float camera_rotation = Pi * 0.17f;
    const float zoom = 37.0f;
    const auto selection = ee::ResolveFloorScreenTransform(
        origin_transform, camera_rotation, zoom);
    const auto selection_point = ee::ApplyFloorScreenTransform(
        selection, sample_x, sample_y);
    const auto world_point = ee::ApplyFloorLocalTransform(
        origin_transform, sample_x, sample_y);
    const float cc = std::cos(camera_rotation);
    const float cs = std::sin(camera_rotation);
    const ee::FloorTransformPoint expected_selection{
        zoom * (cc * world_point.x + cs * world_point.y),
        zoom * (cs * world_point.x - cc * world_point.y)};
    if (!Near(selection_point.x, expected_selection.x, 0.001f) ||
        !Near(selection_point.y, expected_selection.y, 0.001f))
    {
        std::cerr << "selection outline transform differs from floor transform\n";
        return 1;
    }

    std::cout << "floor anisotropic transform order passed\n";
    return 0;
}
