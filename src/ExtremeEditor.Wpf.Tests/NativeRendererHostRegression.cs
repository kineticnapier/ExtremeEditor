using System.Reflection;
using System.Windows.Interop;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class NativeRendererHostRegression
{
    public static void Run()
    {
        using var source = new HwndSource(new HwndSourceParameters("NativeRendererHostRegression")
        {
            Width = 320,
            Height = 200,
            WindowStyle = unchecked((int)0x80000000)
        });

        if (source.Handle == nint.Zero)
            throw new InvalidOperationException("Regression host HWND was not created.");

        Type sessionType = typeof(MainWindow).Assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererSession")
            ?? throw new InvalidOperationException("NativeRendererSession is missing.");

        MethodInfo create = sessionType.GetMethod(
            "Create",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NativeRendererSession.Create is missing.");

        object session = create.Invoke(null, [source.Handle, 320u, 200u])
            ?? throw new InvalidOperationException("NativeRendererSession.Create returned null.");

        try
        {
            PropertyInfo childHwnd = sessionType.GetProperty(
                "ChildHwnd",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("NativeRendererSession.ChildHwnd is missing.");

            object? rawChild = childHwnd.GetValue(session);
            if (rawChild is not nint child || child == nint.Zero)
                throw new InvalidOperationException("Native child HWND was not created.");
        }
        finally
        {
            if (session is IDisposable disposable)
                disposable.Dispose();
            else
                throw new InvalidOperationException("NativeRendererSession must implement IDisposable.");
        }
    }
}
