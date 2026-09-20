using System.Runtime.InteropServices;

namespace ExtremeEditor.Wpf.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAbiInfo
{
    public uint StructSize;
    public uint ApiVersion;
    public uint FloorSize;
    public uint ClockSize;
    public uint DiagnosticsSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRendererCreateInfo
{
    public uint StructSize;
    public uint Width;
    public uint Height;
    public uint Flags;
}
