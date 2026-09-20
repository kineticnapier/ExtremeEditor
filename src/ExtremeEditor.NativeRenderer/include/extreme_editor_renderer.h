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
#define EE_ICON_NONE 0xffffffffu
#define EE_ICON_FLAG_FLOOR 0x1u
#define EE_ICON_FLAG_FLIPPED 0x2u
#define EE_PLAYBACK_TIMING_FLAG_CCW 0x1u
#define EE_PLAYBACK_FLAG_ACTIVE 0x1u
#define EE_PLAYBACK_FLAG_PLAYING 0x2u

#ifdef __cplusplus
extern "C" {
#endif

typedef void* EeRendererHandle;
typedef void (__cdecl *EeSelectionChangedCallback)(void* user_data, int32_t floor);
typedef void (__cdecl *EeFollowPlayerChangedCallback)(void* user_data, int32_t enabled);

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

typedef struct EeFloor
{
    float x;
    float y;
    float entry_angle;
    uint32_t geometry_id;
    uint32_t icon_id;
    uint32_t icon_flags;
    float icon_angle;
} EeFloor;

typedef struct EeGeometry
{
    uint32_t point_offset;
    uint32_t point_count;
} EeGeometry;

typedef struct EePoint
{
    float x;
    float y;
} EePoint;

typedef struct EePlaybackTiming
{
    double entry_time;
    double exit_time;
    double pause_seconds;
    float entry_angle;
    float angle_moved;
    uint32_t flags;
    uint32_t reserved;
} EePlaybackTiming;

EE_RENDERER_API uint32_t ee_renderer_get_api_version(void);
EE_RENDERER_API EeResult ee_renderer_get_abi_info(EeAbiInfo* info);
EE_RENDERER_API EeResult ee_renderer_create(
    HWND parent,
    const EeRendererCreateInfo* info,
    EeRendererHandle* out_renderer);
EE_RENDERER_API void ee_renderer_destroy(EeRendererHandle renderer);
EE_RENDERER_API HWND ee_renderer_get_child_hwnd(EeRendererHandle renderer);
EE_RENDERER_API void ee_renderer_resize(EeRendererHandle renderer, uint32_t width, uint32_t height);
EE_RENDERER_API EeResult ee_renderer_set_level(
    EeRendererHandle renderer,
    const EeFloor* floors,
    uint32_t floor_count,
    const EeGeometry* geometries,
    uint32_t geometry_count,
    const EePoint* points,
    uint32_t point_count,
    float bounds_left,
    float bounds_top,
    float bounds_right,
    float bounds_bottom);
EE_RENDERER_API void ee_renderer_frame_all(EeRendererHandle renderer);
EE_RENDERER_API void ee_renderer_clear_icon_assets(EeRendererHandle renderer);
EE_RENDERER_API EeResult ee_renderer_set_icon_asset(
    EeRendererHandle renderer,
    uint32_t icon_id,
    const wchar_t* image_path,
    const wchar_t* outline_path);
EE_RENDERER_API int32_t ee_renderer_get_selected_floor(EeRendererHandle renderer);
EE_RENDERER_API void ee_renderer_set_selection_changed_callback(
    EeRendererHandle renderer,
    EeSelectionChangedCallback callback,
    void* user_data);
EE_RENDERER_API EeResult ee_renderer_set_playback_timeline(
    EeRendererHandle renderer,
    const EePlaybackTiming* timings,
    uint32_t timing_count);
EE_RENDERER_API void ee_renderer_set_playback_anchor(
    EeRendererHandle renderer,
    double chart_time,
    double chart_rate,
    uint32_t flags);
EE_RENDERER_API void ee_renderer_set_follow_player(EeRendererHandle renderer, int32_t enabled);
EE_RENDERER_API void ee_renderer_set_follow_player_changed_callback(
    EeRendererHandle renderer,
    EeFollowPlayerChangedCallback callback,
    void* user_data);

#ifdef __cplusplus
}
#endif
