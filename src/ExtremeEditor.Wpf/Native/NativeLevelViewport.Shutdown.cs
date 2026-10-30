using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

public sealed partial class NativeLevelViewport
{
    internal void ShutdownRenderer()
    {
        ShutdownDiagnostics.Begin("NativeViewport.ShutdownRenderer");
        NativeRendererSession? session = _session;
        // The camera cache holds references to a native session and a possibly
        // large timeline. Clear it even when the renderer was already destroyed
        // by HwndHost, so shutdown cannot retain a disposed session.
        _session = null;
        _playbackAnchorGate.Invalidate();
        _cameraUploadSession = null;
        _cameraUploadedSource = null;
        _cameraUploadedHeight = 0;
        if (session is null)
        {
            ShutdownDiagnostics.Complete("NativeViewport.ShutdownRenderer", "session=null");
            return;
        }

        session.SelectionChanged -= NativeSelectionChanged;
        session.FollowPlayerChanged -= NativeFollowPlayerChanged;
        session.EditorActionRequested -= NativeEditorActionRequested;
        _session = null;
        _playbackAnchorGate.Invalidate();
        session.Dispose();
        ShutdownDiagnostics.Complete("NativeViewport.ShutdownRenderer");
    }
}
