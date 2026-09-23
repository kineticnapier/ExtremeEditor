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

#define EE_RENDERER_API_VERSION 10u
#define EE_ICON_NONE 0xffffffffu
#define EE_ICON_FLAG_FLOOR 0x1u
#define EE_ICON_FLAG_FLIPPED 0x2u
#define EE_PLAYBACK_TIMING_FLAG_CCW 0x1u
#define EE_PLAYBACK_FLAG_ACTIVE 0x1u
#define EE_PLAYBACK_FLAG_PLAYING 0x2u
#define EE_INPUT_MODIFIER_SHIFT 0x1u
#define EE_INPUT_MODIFIER_CONTROL 0x2u
#define EE_INPUT_MODIFIER_ALT 0x4u
#define EE_INPUT_MODIFIER_WINDOWS 0x8u
#define EE_CAMERA_TARGET_PLAYER_X 0x1u
#define EE_CAMERA_TARGET_PLAYER_Y 0x2u
#define EE_CAMERA_REFERENCE_TILE 0x4u
#define EE_CAMERA_APPLY_X 0x100u
#define EE_CAMERA_APPLY_Y 0x200u
#define EE_CAMERA_APPLY_ROTATION 0x400u
#define EE_CAMERA_APPLY_ZOOM 0x800u
#define EE_TRACK_VISUAL_ENABLED 0x80000000u
#define EE_TRACK_VISUAL_COLOR_TYPE_MASK 0x7u
#define EE_TRACK_VISUAL_STYLE_SHIFT 3u
#define EE_TRACK_VISUAL_STYLE_MASK (0x7u << EE_TRACK_VISUAL_STYLE_SHIFT)
#define EE_TRACK_VISUAL_PULSE_SHIFT 6u
#define EE_TRACK_VISUAL_PULSE_MASK (0x3u << EE_TRACK_VISUAL_PULSE_SHIFT)
#define EE_TRACK_VISUAL_USE_TEXTURE 0x100u
#define EE_TRACK_VISUAL_CUSTOM_TEXTURE 0x200u
#define EE_TRACK_TRANSFORM_ENABLED 0x1u
#define EE_TRACK_STICK_TO_FLOORS 0x2u
#define EE_TRACK_TRANSFORM_X 0x1u
#define EE_TRACK_TRANSFORM_Y 0x2u
#define EE_TRACK_TRANSFORM_ROTATION 0x4u
#define EE_TRACK_TRANSFORM_SCALE_X 0x8u
#define EE_TRACK_TRANSFORM_SCALE_Y 0x10u
#define EE_TRACK_TRANSFORM_OPACITY 0x20u

#ifdef __cplusplus
extern "C" {
#endif

typedef void* EeRendererHandle;
typedef void (__cdecl *EeSelectionChangedCallback)(void* user_data, int32_t floor, uint32_t modifiers);
typedef void (__cdecl *EeFollowPlayerChangedCallback)(void* user_data, int32_t enabled);
typedef void (__cdecl *EeEditorActionCallback)(
    void* user_data,
    uint32_t action,
    int32_t floor,
    double value);

typedef enum EeEditorAction
{
    EE_EDITOR_ACTION_NONE = 0,
    EE_EDITOR_ACTION_INSERT_ANGLE = 1,
    EE_EDITOR_ACTION_DELETE = 2,
    EE_EDITOR_ACTION_ROTATE_180 = 3,
    EE_EDITOR_ACTION_INSERT_MIDSPIN = 4,
    EE_EDITOR_ACTION_INSERT_FULL_TURN = 5
} EeEditorAction;

typedef enum EeResult
{
    EE_OK = 0,
    EE_ERROR_INVALID_ARGUMENT = 1,
    EE_ERROR_ABI_MISMATCH = 2,
    EE_ERROR_INITIALIZATION = 3,
    EE_ERROR_DEVICE_LOST = 4
} EeResult;

typedef enum EeCameraEase
{
    EE_CAMERA_EASE_LINEAR = 0,
    EE_CAMERA_EASE_IN_SINE = 1,
    EE_CAMERA_EASE_OUT_SINE = 2,
    EE_CAMERA_EASE_IN_OUT_SINE = 3,
    EE_CAMERA_EASE_IN_QUAD = 4,
    EE_CAMERA_EASE_OUT_QUAD = 5,
    EE_CAMERA_EASE_IN_OUT_QUAD = 6,
    EE_CAMERA_EASE_IN_CUBIC = 7,
    EE_CAMERA_EASE_OUT_CUBIC = 8,
    EE_CAMERA_EASE_IN_OUT_CUBIC = 9,
    EE_CAMERA_EASE_IN_QUART = 10,
    EE_CAMERA_EASE_OUT_QUART = 11,
    EE_CAMERA_EASE_IN_OUT_QUART = 12,
    EE_CAMERA_EASE_IN_QUINT = 13,
    EE_CAMERA_EASE_OUT_QUINT = 14,
    EE_CAMERA_EASE_IN_OUT_QUINT = 15,
    EE_CAMERA_EASE_IN_EXPO = 16,
    EE_CAMERA_EASE_OUT_EXPO = 17,
    EE_CAMERA_EASE_IN_OUT_EXPO = 18,
    EE_CAMERA_EASE_IN_CIRC = 19,
    EE_CAMERA_EASE_OUT_CIRC = 20,
    EE_CAMERA_EASE_IN_OUT_CIRC = 21,
    EE_CAMERA_EASE_IN_BACK = 22,
    EE_CAMERA_EASE_OUT_BACK = 23,
    EE_CAMERA_EASE_IN_OUT_BACK = 24,
    EE_CAMERA_EASE_IN_ELASTIC = 25,
    EE_CAMERA_EASE_OUT_ELASTIC = 26,
    EE_CAMERA_EASE_IN_OUT_ELASTIC = 27,
    EE_CAMERA_EASE_IN_BOUNCE = 28,
    EE_CAMERA_EASE_OUT_BOUNCE = 29,
    EE_CAMERA_EASE_IN_OUT_BOUNCE = 30
} EeCameraEase;

typedef struct EeAbiInfo
{
    uint32_t struct_size;
    uint32_t api_version;
    uint32_t floor_size;
    uint32_t clock_size;
    uint32_t diagnostics_size;
    uint32_t track_transform_event_size;
    uint32_t camera_event_size;
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
    uint32_t track_primary_color;
    uint32_t track_secondary_color;
    uint32_t track_visual_flags;
    float track_anim_duration;
    float track_glow_intensity;
    int32_t track_start_floor;
    uint32_t track_pulse_length;
    float transform_scale_x;
    float transform_scale_y;
    float transform_opacity;
    uint32_t track_transform_flags;
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

typedef struct EeCameraEvent
{
    double start_time;
    double duration_seconds;
    float start_x;
    float start_y;
    float target_x;
    float target_y;
    float start_rotation;
    float target_rotation;
    float start_zoom;
    float target_zoom;
    uint32_t flags;
    uint32_t ease;
    int32_t reference_floor;
    uint32_t reference_flags;
} EeCameraEvent;

typedef struct EeTrackTransformEvent
{
    double start_time;
    double duration_seconds;
    int32_t floor;
    uint32_t flags;
    float start_x;
    float start_y;
    float target_x;
    float target_y;
    float start_rotation;
    float target_rotation;
    float start_scale_x;
    float start_scale_y;
    float target_scale_x;
    float target_scale_y;
    float start_opacity;
    float target_opacity;
    uint32_t ease;
    uint32_t reserved;
} EeTrackTransformEvent;

typedef struct EeRendererDiagnostics
{
    uint32_t struct_size;
    uint32_t reserved;
    double fps;
    double frame_ms;
    double max_frame_ms;
    double render_ms;
    double cull_ms;
    double update_ms;
    double draw_setup_ms;
    double floor_ms;
    double icon_ms;
    double overlay_ms;
    double end_draw_ms;
    double present_ms;
    double track_total_ms;
    double track_clock_ms;
    double track_evaluate_ms;
    double track_apply_ms;
    double track_spatial_remove_ms;
    double track_spatial_insert_ms;
    uint32_t visible_candidates;
    uint32_t floor_draws;
    uint32_t icon_draws;
    uint32_t draw_calls;
    uint32_t track_total_count;
    uint32_t track_active_count;
    uint32_t track_admitted_count;
    uint32_t track_finished_count;
    uint64_t track_event_scan_count;
    uint64_t track_max_event_scan_count;
} EeRendererDiagnostics;

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
EE_RENDERER_API void ee_renderer_set_selection(
    EeRendererHandle renderer,
    const int32_t* floors,
    uint32_t floor_count,
    int32_t primary_floor);
EE_RENDERER_API void ee_renderer_set_selection_changed_callback(
    EeRendererHandle renderer,
    EeSelectionChangedCallback callback,
    void* user_data);
EE_RENDERER_API void ee_renderer_set_editor_action_callback(
    EeRendererHandle renderer,
    EeEditorActionCallback callback,
    void* user_data);
EE_RENDERER_API EeResult ee_renderer_set_playback_timeline(
    EeRendererHandle renderer,
    const EePlaybackTiming* timings,
    uint32_t timing_count);
EE_RENDERER_API EeResult ee_renderer_set_camera_timeline(
    EeRendererHandle renderer,
    const EeCameraEvent* events,
    uint32_t event_count);
EE_RENDERER_API EeResult ee_renderer_set_track_transform_timeline(
    EeRendererHandle renderer,
    const EeTrackTransformEvent* events,
    uint32_t event_count);
EE_RENDERER_API void ee_renderer_set_playback_anchor(
    EeRendererHandle renderer,
    double chart_time,
    double chart_rate,
    uint32_t flags);
EE_RENDERER_API void ee_renderer_set_track_playback_anchor(
    EeRendererHandle renderer,
    double chart_time,
    double chart_rate,
    uint32_t flags);
EE_RENDERER_API void ee_renderer_set_follow_player(EeRendererHandle renderer, int32_t enabled);
EE_RENDERER_API void ee_renderer_set_follow_player_changed_callback(
    EeRendererHandle renderer,
    EeFollowPlayerChangedCallback callback,
    void* user_data);
EE_RENDERER_API EeResult ee_renderer_get_diagnostics(
    EeRendererHandle renderer,
    EeRendererDiagnostics* diagnostics);

#ifdef __cplusplus
}
#endif