using System.Runtime.InteropServices;

namespace ExtremeEditor.Wpf.Native;

internal static class NativeRendererNative
{
    private const string DllName = "ExtremeEditor.NativeRenderer.dll";

    internal const uint PlaybackFlagActive = 1u;
    internal const uint PlaybackFlagPlaying = 2u;
    internal const uint InputModifierShift = 1u;
    internal const uint InputModifierControl = 2u;
    internal const uint InputModifierAlt = 4u;
    internal const uint InputModifierWindows = 8u;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SelectionChangedCallback(nint userData, int floor, uint modifiers);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FollowPlayerChangedCallback(nint userData, int enabled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void EditorActionCallback(nint userData, uint action, int floor, double value);

    [DllImport(DllName, EntryPoint = "ee_renderer_get_api_version", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint GetApiVersion();

    [DllImport(DllName, EntryPoint = "ee_renderer_get_abi_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetAbiInfo(ref NativeAbiInfo info);

    [DllImport(DllName, EntryPoint = "ee_renderer_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Create(
        nint parent,
        ref NativeRendererCreateInfo info,
        out nint renderer);

    [DllImport(DllName, EntryPoint = "ee_renderer_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(nint renderer);

    [DllImport(DllName, EntryPoint = "ee_renderer_get_child_hwnd", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint GetChildHwnd(nint renderer);

    [DllImport(DllName, EntryPoint = "ee_renderer_resize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Resize(nint renderer, uint width, uint height);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_level", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetLevel(
        nint renderer,
        nint floors,
        uint floorCount,
        nint geometries,
        uint geometryCount,
        nint points,
        uint pointCount,
        float boundsLeft,
        float boundsTop,
        float boundsRight,
        float boundsBottom);

    [DllImport(DllName, EntryPoint = "ee_renderer_frame_all", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FrameAll(nint renderer);

    [DllImport(DllName, EntryPoint = "ee_renderer_clear_icon_assets", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ClearIconAssets(nint renderer);

    [DllImport(
        DllName,
        EntryPoint = "ee_renderer_set_icon_asset",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    internal static extern int SetIconAsset(
        nint renderer,
        uint iconId,
        string imagePath,
        string? outlinePath);

    [DllImport(DllName, EntryPoint = "ee_renderer_get_selected_floor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetSelectedFloor(nint renderer);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_selection", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetSelection(
        nint renderer,
        nint floors,
        uint floorCount,
        int primaryFloor);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_selection_changed_callback", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetSelectionChangedCallback(
        nint renderer,
        SelectionChangedCallback? callback,
        nint userData);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_editor_action_callback", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetEditorActionCallback(
        nint renderer,
        EditorActionCallback? callback,
        nint userData);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_playback_timeline", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetPlaybackTimeline(
        nint renderer,
        nint timings,
        uint timingCount);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_camera_timeline", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetCameraTimeline(
        nint renderer,
        nint events,
        uint eventCount);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_track_transform_timeline", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetTrackTransformTimeline(
        nint renderer,
        nint events,
        uint eventCount);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_playback_anchor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetPlaybackAnchor(
        nint renderer,
        double chartTime,
        double chartRate,
        uint flags);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_track_playback_anchor", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetTrackPlaybackAnchor(
        nint renderer,
        double chartTime,
        double chartRate,
        uint flags);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_follow_player", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetFollowPlayer(nint renderer, int enabled);

    [DllImport(DllName, EntryPoint = "ee_renderer_set_follow_player_changed_callback", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetFollowPlayerChangedCallback(
        nint renderer,
        FollowPlayerChangedCallback? callback,
        nint userData);

    [DllImport(DllName, EntryPoint = "ee_renderer_get_diagnostics", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetDiagnostics(
        nint renderer,
        ref NativeRendererDiagnostics diagnostics);
}
