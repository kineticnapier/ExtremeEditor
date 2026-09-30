namespace ExtremeEditor.Wpf.Native;

public sealed partial class NativeLevelViewport
{
    internal void ShutdownRenderer()
    {
        NativeRendererSession? session = _session;
        if (session is null)
            return;

        session.SelectionChanged -= NativeSelectionChanged;
        session.FollowPlayerChanged -= NativeFollowPlayerChanged;
        session.EditorActionRequested -= NativeEditorActionRequested;
        _session = null;
        session.Dispose();
    }
}
