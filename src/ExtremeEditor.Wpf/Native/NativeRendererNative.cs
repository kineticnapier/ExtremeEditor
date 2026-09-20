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
}
