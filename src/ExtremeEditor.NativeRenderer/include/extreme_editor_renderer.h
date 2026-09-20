#pragma once

#include <stdint.h>
#include <windows.h>

#if defined(_WIN32)
    #if defined(EE_RENDERER_BUILD)
        #define EE_RENDERER_API __declspec(dllexport)
    #else
        #define EE_RENDERER_API __declspec(dllimport)
    #endif
#else
    #define EE_RENDERER_API
#endif

#define EE_RENDERER_API_VERSION 1u

#ifdef __cplusplus
extern "C" {
#endif

typedef void* EeRendererHandle;

typedef enum EeResult
{
    EE_OK = 0,
    EE_ERROR_INVALID_ARGUMENT = 1,
    EE_ERROR_ABI_MISMATCH = 2,
    EE_ERROR_INITIALIZATION = 3,
    EE_ERROR_DEVICE_LOST = 4
} EeResult;

typedef struct EeAbiInfo
{
    uint32_t struct_size;
    uint32_t api_version;
    uint32_t floor_size;
    uint32_t clock_size;
    uint32_t diagnostics_size;
} EeAbiInfo;

typedef struct EeRendererCreateInfo
{
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t flags;
} EeRendererCreateInfo;

EE_RENDERER_API uint32_t ee_renderer_get_api_version(void);
EE_RENDERER_API EeResult ee_renderer_get_abi_info(EeAbiInfo* info);
EE_RENDERER_API EeResult ee_renderer_create(
    HWND parent,
    const EeRendererCreateInfo* info,
    EeRendererHandle* out_renderer);
EE_RENDERER_API void ee_renderer_destroy(EeRendererHandle renderer);
EE_RENDERER_API HWND ee_renderer_get_child_hwnd(EeRendererHandle renderer);
EE_RENDERER_API void ee_renderer_resize(EeRendererHandle renderer, uint32_t width, uint32_t height);

#ifdef __cplusplus
}
#endif
