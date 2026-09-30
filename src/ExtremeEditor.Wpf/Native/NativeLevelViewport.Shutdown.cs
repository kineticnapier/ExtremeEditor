using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

public sealed partial class NativeLevelViewport
{
    internal void ShutdownRenderer()
    {
        ShutdownDiagnostics.Begin("NativeViewport.ShutdownRenderer");
        NativeRendererSession? session = _session;
        if (session is null)
        {
            ShutdownDiagnostics.Complete("NativeViewport.ShutdownRenderer", "session=null");
            return;
        }

        session.SelectionChanged -= NativeSelectionChanged;
        session.FollowPlayerChanged -= NativeFollowPlayerChanged;
        session.EditorActionRequested -= NativeEditorActionRequested;
        _session = null;
        session.Dispose();
        ShutdownDiagnostics.Complete("NativeViewport.ShutdownRenderer");
    }
}
