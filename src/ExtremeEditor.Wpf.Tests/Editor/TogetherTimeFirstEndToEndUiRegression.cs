using System.IO;
using System.Xml.Linq;

namespace ExtremeEditor.Wpf.Tests;

/// <summary>
/// Phase H: structural gate for all time-first controls. Core behavioral
/// transport/export tests remain separate; Windows manual UI testing is
/// required before release (no GUI automation).
/// </summary>
internal static class TogetherTimeFirstEndToEndUiRegression
{
    public static void Run()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
            "src", "ExtremeEditor.Wpf", "Dialogs", "TogetherWorkspaceWindow.xaml")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Repository root was not found.");

        string path = Path.Combine(root.FullName, "src", "ExtremeEditor.Wpf",
            "Dialogs", "TogetherWorkspaceWindow.xaml");
        XDocument xaml = XDocument.Load(path);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string name in new[] {
            "CloneWorkspaceButton", "EntryAngleBox", "SetEntryAngleButton",
            "ExportBpmBox", "ExportOffsetBox", "ExportPitchBox",
            "ExportCountdownBox", "ExportSeparateCountdownBox" })
            Check(xaml.Descendants().Any(e => (string?)e.Attribute(ns + "Name") == name),
                "Missing Phase H time-first control: " + name);

        string code = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        foreach (string required in new[] {
            "TimeFirstSegmentGitRemoteSync.ConnectLocalBare",
            "TimeFirstSegmentGitRemoteSync.CloneLocalBare",
            "TimeFirstSegmentGitRemoteSync.Fetch",
            "TimeFirstSegmentGitRemoteSync.Pull",
            "TimeFirstSegmentGitRemoteSync.Push",
            "TimeFirstSegmentAdoFaiExporter.ComposeAndValidate",
            "TimeFirstSegmentAdoFaiExporter.ExportValidated",
            "RefuseUnsafeTimeEditorSwitch", "ValidateLoadedTimeAudio",
            "TimeFirstAdoFaiSettings", "SetEntryAngleClick" })
            Check(code.Contains(required, StringComparison.Ordinal),
                "Phase H time-first flow does not invoke " + required);

        Check(File.Exists(Path.Combine(root.FullName, "docs",
                "together-time-first-phase-h-manual.md")),
            "Manual two-client time-first E2E instructions are missing.");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
