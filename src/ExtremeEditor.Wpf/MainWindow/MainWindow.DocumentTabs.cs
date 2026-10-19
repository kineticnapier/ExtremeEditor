using System.Windows.Threading;

namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    // Tab ownership is centralized here even while MainWindow hosts only one
    // editor viewport. The future multi-tab step will keep per-tab view state.
    private readonly EditorDocumentTabs _documentTabs = new();

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
        EditorDocumentTab? active = _documentTabs.ActiveTab;
        if (active is null)
        {
            ActiveDocumentTabTitle.Text = "Untitled.adofai";
            ActiveDocumentTab.ToolTip = null;
            return;
        }

        ActiveDocumentTabTitle.Text = active.Header;
        string? path = active.Document.SourcePath;
        ActiveDocumentTab.ToolTip = path is null or "<synthetic>" ? null : path;
    }
}
