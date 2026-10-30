using System.IO;
using System.Reflection;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class SavePathReservationRegression
{
    internal static void Run()
    {
        Type tabsType = typeof(EditorDocumentTabs);
        MethodInfo reserve = tabsType.GetMethod(
            "TryReserveSavePath",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "RED: document tabs do not reserve Save As destinations across tabs.");

        var tabs = new EditorDocumentTabs();
        EditorDocumentTab first = NewTab();
        EditorDocumentTab second = NewTab();
        Require(tabs.Add(first) && tabs.Add(second, activate: false), "test tabs were not added.");

        string directory = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-SaveReservation-{Guid.NewGuid():N}");
        string shared = Path.Combine(directory, "shared.adofai");
        string other = Path.Combine(directory, "other.adofai");

        IDisposable firstLease = Reserve(reserve, tabs, first, shared)
            ?? throw new InvalidOperationException("RED: the first Save As destination was not reserved.");
        try
        {
            Require(Reserve(reserve, tabs, second, shared) is null,
                "RED: another tab acquired an already-reserved save destination.");
            Require(Reserve(reserve, tabs, first, other) is null,
                "RED: one tab started a second Save As while its first save was pending.");
            using IDisposable? independent = Reserve(reserve, tabs, second, other);
            Require(independent is not null,
                "Different tabs must be able to save to different destinations concurrently.");
        }
        finally
        {
            firstLease.Dispose();
        }

        using IDisposable? released = Reserve(reserve, tabs, second, shared);
        Require(released is not null, "A completed/failed save must release its destination.");

        string source = ReadSource("src/ExtremeEditor.Wpf/MainWindow/MainWindow.Editor.cs");
        Require(source.Contains("ReferenceEquals(_documentTabs.ActiveTab, saveTab)", StringComparison.Ordinal),
            "RED: async save completion does not verify which tab owns the visible title/status.");
    }

    private static IDisposable? Reserve(
        MethodInfo method,
        EditorDocumentTabs tabs,
        EditorDocumentTab tab,
        string path) => method.Invoke(tabs, [tab, path]) as IDisposable;

    private static EditorDocumentTab NewTab()
    {
        LevelDocument document = LevelDocument.CreateSynthetic(2);
        return new EditorDocumentTab(document, new EditorSession(document));
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"RED setup failure: source not found: {relativePath}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
