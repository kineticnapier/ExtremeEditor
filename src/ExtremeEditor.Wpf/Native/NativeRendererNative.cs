using System.Runtime.InteropServices;

namespace ExtremeEditor.Wpf.Native;

internal static class NativeRendererNative
{
    private const string DllName = "ExtremeEditor.NativeRenderer.dll";

    [DllImport(DllName, EntryPoint = "ee_renderer_get_api_version", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint GetApiVersion();

    [DllImport(DllName, EntryPoint = "ee_renderer_get_abi_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetAbiInfo(ref NativeAbiInfo info);
}
