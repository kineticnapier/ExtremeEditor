#include "extreme_editor_renderer.h"

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
