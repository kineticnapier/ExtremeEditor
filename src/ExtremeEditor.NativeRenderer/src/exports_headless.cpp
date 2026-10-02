#include "extreme_editor_headless.h"
#include "renderer.h"

#include <cmath>
#include <new>

EeResult ee_renderer_create_headless(
    const EeRendererCreateInfo* info,
    EeRendererHandle* out_renderer)
{
    if (info == nullptr || out_renderer == nullptr)
        return EE_ERROR_INVALID_ARGUMENT;

    *out_renderer = nullptr;
    if (info->struct_size != sizeof(EeRendererCreateInfo))
        return EE_ERROR_ABI_MISMATCH;
    if (info->width == 0u || info->height == 0u)
        return EE_ERROR_INVALID_ARGUMENT;

    try
    {
        auto* renderer = new ee::Renderer();
        if (!renderer->InitializeHeadless(info->width, info->height))
        {
            delete renderer;
            return EE_ERROR_INITIALIZATION;
        }
        *out_renderer = renderer;
        return EE_OK;
    }
    catch (...)
    {
        return EE_ERROR_INITIALIZATION;
    }
}

EeResult ee_renderer_render_rgb(
    EeRendererHandle renderer,
    double scene_time,
    double visual_time,
    uint8_t* rgb,
    uint32_t rgb_size,
    uint32_t row_stride)
{
    if (renderer == nullptr || rgb == nullptr || rgb_size == 0u || row_stride == 0u ||
        !std::isfinite(scene_time) || !std::isfinite(visual_time))
        return EE_ERROR_INVALID_ARGUMENT;

    return static_cast<ee::Renderer*>(renderer)->RenderHeadlessRgb(
        scene_time,
        visual_time,
        rgb,
        rgb_size,
        row_stride)
        ? EE_OK
        : EE_ERROR_DEVICE_LOST;
}
