using System.Windows.Threading;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    // One active tab for now. A later step will add a tab collection and give
    // each workspace its own native viewport and audio session.
    private EditorDocumentTab? _activeDocumentTab;

    private void ActiveEditorSessionChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            if (!_isClosed && ReferenceEquals(sender, _editor))
                RefreshActiveDocumentTab();
            return;
        }

        // SaveAsync may raise EditorSession.Changed off the UI thread.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_isClosed && ReferenceEquals(sender, _editor))
                RefreshActiveDocumentTab();
        }));
    }

    private void RefreshActiveDocumentTab()
    {
        if (_activeDocumentTab is null)
        {
            ActiveDocumentTabTitle.Text = "Untitled.adofai";
            ActiveDocumentTab.ToolTip = null;
            return;
        }

        ActiveDocumentTabTitle.Text = _activeDocumentTab.Header;
        string? path = _activeDocumentTab.Document.SourcePath;
        ActiveDocumentTab.ToolTip = path is null or "<synthetic>" ? null : path;
    }
}
