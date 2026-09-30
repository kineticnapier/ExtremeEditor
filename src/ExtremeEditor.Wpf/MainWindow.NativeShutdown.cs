using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    internal void ShutdownNativeRenderer()
    {
        ShutdownDiagnostics.Begin("MainWindow.ShutdownNativeRenderer");
        NativeViewport.ShutdownRenderer();
        ShutdownDiagnostics.Complete("MainWindow.ShutdownNativeRenderer");
    }
}
