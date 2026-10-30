namespace ExtremeEditor.Wpf;

internal enum UnsavedDocumentCloseChoice
{
    Save,
    Discard,
    Cancel
}

internal static class UnsavedDocumentCloseCoordinator
{
    internal static async Task<bool> ConfirmAsync(
        IReadOnlyList<EditorDocumentTab> tabs,
        Func<EditorDocumentTab, UnsavedDocumentCloseChoice> prompt,
        Func<EditorDocumentTab, Task<bool>> saveAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(saveAsync);

        foreach (EditorDocumentTab tab in tabs.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tab.IsDirty)
                continue;

            switch (prompt(tab))
            {
                case UnsavedDocumentCloseChoice.Discard:
                    continue;
                case UnsavedDocumentCloseChoice.Cancel:
                    return false;
                case UnsavedDocumentCloseChoice.Save:
                    if (!await saveAsync(tab).ConfigureAwait(true) || tab.IsDirty)
                        return false;
                    break;
                default:
                    throw new InvalidOperationException("Unknown window-close choice.");
            }
        }

        return true;
    }
}
