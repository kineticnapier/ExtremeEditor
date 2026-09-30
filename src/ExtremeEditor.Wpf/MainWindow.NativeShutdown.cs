namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    internal void ShutdownNativeRenderer()
    {
        NativeViewport.ShutdownRenderer();
    }
}
