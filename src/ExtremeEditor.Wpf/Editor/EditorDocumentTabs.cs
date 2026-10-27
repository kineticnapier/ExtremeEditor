using System.Collections.ObjectModel;
using System.IO;

namespace ExtremeEditor.Wpf;

/// <summary>
/// Holds chart identity, independent editor sessions and selection order.
/// This does not own playback, native views or window controls yet.
/// </summary>
internal sealed class EditorDocumentTabs
{
    private readonly List<EditorDocumentTab> _tabs = [];
    private readonly ReadOnlyCollection<EditorDocumentTab> _readOnlyTabs;
    private readonly Dictionary<string, EditorDocumentTab> _savePathReservations =
        new(StringComparer.OrdinalIgnoreCase);

    internal EditorDocumentTabs()
    {
        _readOnlyTabs = _tabs.AsReadOnly();
    }

    internal IReadOnlyList<EditorDocumentTab> Tabs => _readOnlyTabs;
    internal EditorDocumentTab? ActiveTab { get; private set; }

    internal bool Add(EditorDocumentTab tab, bool activate = true)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (ContainsDocument(tab) || HasSamePath(tab))
            return false;

        _tabs.Add(tab);
        if (activate || ActiveTab is null)
            ActiveTab = tab;
        return true;
    }

    internal bool Activate(EditorDocumentTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!_tabs.Contains(tab))
            return false;

        ActiveTab = tab;
        return true;
    }

    internal bool Remove(EditorDocumentTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        int index = _tabs.IndexOf(tab);
        if (index < 0)
            return false;

        bool wasActive = ReferenceEquals(tab, ActiveTab);
        _tabs.RemoveAt(index);
        if (wasActive)
        {
            // Prefer the next tab, otherwise the preceding one.
            ActiveTab = _tabs.Count == 0
                ? null
                : _tabs[Math.Min(index, _tabs.Count - 1)];
        }
        return true;
    }

    internal EditorDocumentTab? FindByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string key = NormalizePath(path);
        EditorDocumentTab? open = _tabs.FirstOrDefault(tab =>
            TryGetSourcePath(tab, out string? existing) &&
            StringComparer.OrdinalIgnoreCase.Equals(existing, key));
        if (open is not null)
            return open;
        return _savePathReservations.TryGetValue(key, out EditorDocumentTab? reserved)
            ? reserved
            : null;
    }

    internal IDisposable? TryReserveSavePath(EditorDocumentTab tab, string path)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!_tabs.Contains(tab) || string.IsNullOrWhiteSpace(path))
            return null;

        string key = NormalizePath(path);
        if (_savePathReservations.ContainsKey(key) ||
            _savePathReservations.Values.Any(owner => ReferenceEquals(owner, tab)))
            return null;

        EditorDocumentTab? existing = FindByPath(key);
        if (existing is not null && !ReferenceEquals(existing, tab))
            return null;

        _savePathReservations.Add(key, tab);
        return new SavePathReservation(this, key, tab);
    }

    // Compatibility bridge: the existing Open flow REPLACES its one chart.
    // Do not use this after enabling multiple live workspaces: that flow must
    // instead Add / Activate without discarding any other tab's edit history.
    internal void ResetToSingle(EditorDocumentTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _tabs.Clear();
        _tabs.Add(tab);
        ActiveTab = tab;
    }

    private bool ContainsDocument(EditorDocumentTab tab) =>
        _tabs.Any(existing => ReferenceEquals(existing.Document, tab.Document) ||
                              ReferenceEquals(existing.Editor, tab.Editor));

    private bool HasSamePath(EditorDocumentTab tab) =>
        TryGetSourcePath(tab, out string? path) &&
        _tabs.Any(existing =>
            TryGetSourcePath(existing, out string? other) &&
            StringComparer.OrdinalIgnoreCase.Equals(path, other));

    private static bool TryGetSourcePath(EditorDocumentTab tab, out string? path)
    {
        path = tab.Document.SourcePath;
        if (string.IsNullOrWhiteSpace(path) || path == "<synthetic>")
        {
            path = null;
            return false;
        }

        path = NormalizePath(path);
        return true;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private void ReleaseSavePath(string key, EditorDocumentTab tab)
    {
        if (_savePathReservations.TryGetValue(key, out EditorDocumentTab? owner) &&
            ReferenceEquals(owner, tab))
            _savePathReservations.Remove(key);
    }

    private sealed class SavePathReservation(
        EditorDocumentTabs owner,
        string key,
        EditorDocumentTab tab) : IDisposable
    {
        private EditorDocumentTabs? _owner = owner;

        public void Dispose()
        {
            EditorDocumentTabs? current = Interlocked.Exchange(ref _owner, null);
            current?.ReleaseSavePath(key, tab);
        }
    }
}
