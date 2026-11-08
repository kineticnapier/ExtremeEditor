#pragma once

#include <cmath>

namespace ee
{
struct FloorTransformPoint
{
    float x = 0.0f;
    float y = 0.0f;
};

struct FloorScreenTransform
{
    float m11 = 1.0f;
    float m12 = 0.0f;
    float m21 = 0.0f;
    float m22 = 1.0f;
};

// Decomposes the accumulated floor angle into the mesh's original entry
// rotation and the transform rotation. The matrix maps a shaped local point
// through R(transform) * S(world X/Y) * R(base entry).
struct FloorLocalTransform
{
    float base_cosine = 1.0f;
    float base_sine = 0.0f;
    float transform_cosine = 1.0f;
    float transform_sine = 0.0f;
    float scale_x = 1.0f;
    float scale_y = 1.0f;
    float xx = 1.0f;
    float xy = 0.0f;
    float yx = 0.0f;
    float yy = 1.0f;
};

inline FloorLocalTransform ResolveFloorLocalTransform(
    float entry_angle,
    float transform_rotation,
    float scale_x,
    float scale_y,
    bool transformed) noexcept
{
    if (!std::isfinite(entry_angle))
        entry_angle = 0.0f;
    if (!transformed || !std::isfinite(transform_rotation))
        transform_rotation = 0.0f;
    if (!transformed || !std::isfinite(scale_x))
        scale_x = 1.0f;
    if (!transformed || !std::isfinite(scale_y))
        scale_y = 1.0f;

    const float base_angle = entry_angle - transform_rotation;
    FloorLocalTransform result;
    result.base_cosine = std::cos(base_angle);
    result.base_sine = std::sin(base_angle);
    result.transform_cosine = std::cos(transform_rotation);
    result.transform_sine = std::sin(transform_rotation);
    result.scale_x = scale_x;
    result.scale_y = scale_y;

    const float cb = result.base_cosine;
    const float sb = result.base_sine;
    const float ct = result.transform_cosine;
    const float st = result.transform_sine;
    result.xx = ct * scale_x * cb - st * scale_y * sb;
    result.xy = -ct * scale_x * sb - st * scale_y * cb;
    result.yx = st * scale_x * cb + ct * scale_y * sb;
    result.yy = -st * scale_x * sb + ct * scale_y * cb;
    return result;
}

inline FloorTransformPoint ApplyFloorLocalTransform(
    const FloorLocalTransform& transform,
    float x,
    float y) noexcept
{
    return {
        transform.xx * x + transform.xy * y,
        transform.yx * x + transform.yy * y};
}

inline FloorScreenTransform ResolveFloorScreenTransform(
    const FloorLocalTransform& transform,
    float camera_rotation,
    float zoom) noexcept
{
    const float camera_cosine = std::cos(camera_rotation);
    const float camera_sine = std::sin(camera_rotation);
    return {
        zoom * (camera_cosine * transform.xx + camera_sine * transform.yx),
        zoom * (camera_sine * transform.xx - camera_cosine * transform.yx),
        zoom * (camera_cosine * transform.xy + camera_sine * transform.yy),
        zoom * (camera_sine * transform.xy - camera_cosine * transform.yy)};
}

inline FloorTransformPoint ApplyFloorScreenTransform(
    const FloorScreenTransform& transform,
    float x,
    float y) noexcept
{
    return {
        transform.m11 * x + transform.m21 * y,
        transform.m12 * x + transform.m22 * y};
}
}
