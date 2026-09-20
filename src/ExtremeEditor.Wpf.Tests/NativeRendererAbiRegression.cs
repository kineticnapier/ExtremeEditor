using System.Reflection;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeRendererAbiRegression
{
    public static void Run()
    {
        Type bridge = typeof(MainWindow).Assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererNative")
            ?? throw new InvalidOperationException("NativeRendererNative is missing.");
        MethodInfo getApiVersion = bridge.GetMethod(
            "GetApiVersion",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NativeRendererNative.GetApiVersion is missing.");

        uint version = (uint)(getApiVersion.Invoke(null, null) ?? 0u);
        if (version != 1u)
            throw new InvalidOperationException($"Native renderer API version mismatch: {version}.");
    }
}
