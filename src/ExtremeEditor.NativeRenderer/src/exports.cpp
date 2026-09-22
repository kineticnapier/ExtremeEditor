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
    info->floor_size = sizeof(EeFloor);
    info->clock_size = sizeof(EePlaybackTiming);
    info->diagnostics_size = sizeof(EeRendererDiagnostics);
    info->track_transform_event_size = sizeof(EeTrackTransformEvent);
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

EeResult ee_renderer_set_level(
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
    float bounds_bottom)
{
    if (renderer == nullptr)
        return EE_ERROR_INVALID_ARGUMENT;

    try
    {
        std::shared_ptr<ee::LevelScene> scene = ee::LevelScene::Create(
            floors,
            floor_count,
            geometries,
            geometry_count,
            points,
            point_count,
            bounds_left,
            bounds_top,
            bounds_right,
            bounds_bottom);
        if (!scene)
            return EE_ERROR_INVALID_ARGUMENT;

        return static_cast<ee::Renderer*>(renderer)->SetLevel(std::move(scene))
            ? EE_OK
            : EE_ERROR_INITIALIZATION;
    }
    catch (...)
    {
        return EE_ERROR_INITIALIZATION;
    }
}

void ee_renderer_frame_all(EeRendererHandle renderer)
{
    if (renderer != nullptr)
        static_cast<ee::Renderer*>(renderer)->FrameAll();
}

void ee_renderer_clear_icon_assets(EeRendererHandle renderer)
{
    if (renderer != nullptr)
        static_cast<ee::Renderer*>(renderer)->ClearIconAssets();
}

EeResult ee_renderer_set_icon_asset(
    EeRendererHandle renderer,
    uint32_t icon_id,
    const wchar_t* image_path,
    const wchar_t* outline_path)
{
    if (renderer == nullptr || image_path == nullptr)
        return EE_ERROR_INVALID_ARGUMENT;

    return static_cast<ee::Renderer*>(renderer)->SetIconAsset(
        icon_id,
        image_path,
        outline_path)
        ? EE_OK
        : EE_ERROR_INVALID_ARGUMENT;
}

int32_t ee_renderer_get_selected_floor(EeRendererHandle renderer)
{
    if (renderer == nullptr)
        return -1;

    return static_cast<ee::Renderer*>(renderer)->SelectedFloor();
}

void ee_renderer_set_selection(
    EeRendererHandle renderer,
    const int32_t* floors,
    uint32_t floor_count,
    int32_t primary_floor)
{
    if (renderer == nullptr || (floor_count > 0 && floors == nullptr))
        return;

    static_cast<ee::Renderer*>(renderer)->SetSelection(floors, floor_count, primary_floor);
}

void ee_renderer_set_selection_changed_callback(
    EeRendererHandle renderer,
    EeSelectionChangedCallback callback,
    void* user_data)
{
    if (renderer != nullptr)
        static_cast<ee::Renderer*>(renderer)->SetSelectionChangedCallback(callback, user_data);
}

void ee_renderer_set_editor_action_callback(
    EeRendererHandle renderer,
    EeEditorActionCallback callback,
    void* user_data)
{
    if (renderer != nullptr)
        static_cast<ee::Renderer*>(renderer)->SetEditorActionCallback(callback, user_data);
}

EeResult ee_renderer_set_playback_timeline(
    EeRendererHandle renderer,
    const EePlaybackTiming* timings,
    uint32_t timing_count)
{
    if (renderer == nullptr || (timing_count > 0 && timings == nullptr))
        return EE_ERROR_INVALID_ARGUMENT;

    return static_cast<ee::Renderer*>(renderer)->SetPlaybackTimeline(timings, timing_count)
        ? EE_OK
        : EE_ERROR_INITIALIZATION;
}

EeResult ee_renderer_set_camera_timeline(
    EeRendererHandle renderer,
    const EeCameraEvent* events,
    uint32_t event_count)
{
    if (renderer == nullptr || (event_count > 0 && events == nullptr))
        return EE_ERROR_INVALID_ARGUMENT;

    return static_cast<ee::Renderer*>(renderer)->SetCameraTimeline(events, event_count)
        ? EE_OK
        : EE_ERROR_INITIALIZATION;
}

EeResult ee_renderer_set_track_transform_timeline(
    EeRendererHandle renderer,
    const EeTrackTransformEvent* events,
    uint32_t event_count)
{
    if (renderer == nullptr || (event_count > 0 && events == nullptr))
        return EE_ERROR_INVALID_ARGUMENT;

    return static_cast<ee::Renderer*>(renderer)->SetTrackTransformTimeline(events, event_count)
        ? EE_OK
        : EE_ERROR_INITIALIZATION;
}

void ee_renderer_set_playback_anchor(
    EeRendererHandle renderer,
    double chart_time,
    double chart_rate,
    uint32_t flags)
{
    if (renderer != nullptr)
        static_cast<ee::Renderer*>(renderer)->SetPlaybackAnchor(chart_time, chart_rate, flags);
}

void ee_renderer_set_track_playback_anchor(
    EeRendererHandle renderer,
    double chart_time,
    double chart_rate,
    uint32_t flags)
{
    if (renderer != nullptr)
        static_cast<ee::Renderer*>(renderer)->SetTrackPlaybackAnchor(chart_time, chart_rate, flags);
}

void ee_renderer_set_follow_player(EeRendererHandle renderer, int32_t enabled)
{
    if (renderer != nullptr)
        static_cast<ee::Renderer*>(renderer)->SetFollowPlayer(enabled != 0);
}

void ee_renderer_set_follow_player_changed_callback(
    EeRendererHandle renderer,
    EeFollowPlayerChangedCallback callback,
    void* user_data)
{
    if (renderer != nullptr)
        static_cast<ee::Renderer*>(renderer)->SetFollowPlayerChangedCallback(callback, user_data);
}

EeResult ee_renderer_get_diagnostics(
    EeRendererHandle renderer,
    EeRendererDiagnostics* diagnostics)
{
    if (renderer == nullptr || diagnostics == nullptr)
        return EE_ERROR_INVALID_ARGUMENT;
    if (diagnostics->struct_size != sizeof(EeRendererDiagnostics))
        return EE_ERROR_ABI_MISMATCH;

    return static_cast<ee::Renderer*>(renderer)->GetDiagnostics(*diagnostics)
        ? EE_OK
        : EE_ERROR_INITIALIZATION;
}
