using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            VerifyFloorGeometryIsCachedAndFrozen();
            Console.WriteLine("PASS: WPF floor geometry is cached and frozen.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            return 1;
        }
    }

    private static void VerifyFloorGeometryIsCachedAndFrozen()
    {
        Assembly assembly = typeof(LevelViewport).Assembly;
        Type rendererType = assembly.GetType("ExtremeEditor.Wpf.WpfFloorRenderer")
            ?? throw new InvalidOperationException("WpfFloorRenderer does not exist yet.");

        MethodInfo method = rendererType.GetMethod(
            "GetCachedGeometry",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WpfFloorRenderer.GetCachedGeometry is missing.");

        object? first = method.Invoke(null, [0f, MathF.PI / 2f, false]);
        object? second = method.Invoke(null, [0f, MathF.PI / 2f, false]);

        if (first is not StreamGeometry geometry)
            throw new InvalidOperationException("GetCachedGeometry must return StreamGeometry.");

        if (!ReferenceEquals(first, second))
            throw new InvalidOperationException("Repeated geometry requests must reuse the cached StreamGeometry instance.");

        if (!geometry.IsFrozen)
            throw new InvalidOperationException("Cached StreamGeometry must be frozen before reuse.");

        Rect bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("Cached floor geometry must have non-empty bounds.");
    }
}
