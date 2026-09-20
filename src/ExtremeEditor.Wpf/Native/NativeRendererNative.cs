using System.Runtime.InteropServices;

namespace ExtremeEditor.Wpf.Native;

internal static class NativeRendererNative
{
    private const string DllName = "ExtremeEditor.NativeRenderer.dll";

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
}
