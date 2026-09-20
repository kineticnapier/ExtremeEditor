#include "extreme_editor_renderer.h"
#include "renderer.h"

#include <new>

uint32_t ee_renderer_get_api_version(void)
{
    return EE_RENDERER_API_VERSION;
}

EeResult ee_renderer_get_abi_info(EeAbiInfo* info)
{
    if (info == nullptr)
        return EE_ERROR_INVALID_ARGUMENT;

    if (info->struct_size != sizeof(EeAbiInfo))
        return EE_ERROR_ABI_MISMATCH;

    info->api_version = EE_RENDERER_API_VERSION;
    info->floor_size = 0u;
    info->clock_size = 0u;
    info->diagnostics_size = 0u;
    return EE_OK;
}

EeResult ee_renderer_create(
    HWND parent,
    const EeRendererCreateInfo* info,
    EeRendererHandle* out_renderer)
{
    if (parent == nullptr || info == nullptr || out_renderer == nullptr)
        return EE_ERROR_INVALID_ARGUMENT;

    *out_renderer = nullptr;

    if (info->struct_size != sizeof(EeRendererCreateInfo))
        return EE_ERROR_ABI_MISMATCH;

    try
    {
        auto* renderer = new ee::Renderer();
        if (!renderer->Initialize(parent, info->width, info->height))
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

void ee_renderer_destroy(EeRendererHandle renderer)
{
    delete static_cast<ee::Renderer*>(renderer);
}

HWND ee_renderer_get_child_hwnd(EeRendererHandle renderer)
{
    if (renderer == nullptr)
        return nullptr;

    return static_cast<ee::Renderer*>(renderer)->ChildHwnd();
}

void ee_renderer_resize(EeRendererHandle renderer, uint32_t width, uint32_t height)
{
    if (renderer == nullptr)
        return;

    static_cast<ee::Renderer*>(renderer)->Resize(width, height);
}
