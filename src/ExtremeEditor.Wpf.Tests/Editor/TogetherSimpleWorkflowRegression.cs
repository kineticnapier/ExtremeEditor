using System.IO;
using System.Xml.Linq;

namespace ExtremeEditor.Wpf.Tests;

internal static class TogetherSimpleWorkflowRegression
{
    public static void Run()
    {
        string repository = FindRepositoryRoot();
        string xamlPath = Path.Combine(repository, "src", "ExtremeEditor.Wpf",
            "Dialogs", "TogetherWorkspaceWindow.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string name in new[]
        {
            "SplitSectionButton", "JoinSectionButton", "MoveBoundaryButton",
            "AssignOwnerButton", "ParticipantNameBox", "ChooseParticipantButton",
            "SectionPlanningPanel", "DetailedRangeExpander"
        })
        {
            Check(document.Descendants().Any(element =>
                    (string?)element.Attribute(x + "Name") == name),
                "The simple Together workflow is missing " + name);
        }

        string xaml = File.ReadAllText(xamlPath);
        foreach (string internalLabel in new[]
        {
            "Header=\"区間ID\"", "Header=\"音楽sample範囲\"",
            "Header=\"床の範囲\""
        })
            Check(!xaml.Contains(internalLabel, StringComparison.Ordinal),
                "The normal section list exposes an internal label: " + internalLabel);

        string code = File.ReadAllText(Path.ChangeExtension(xamlPath, ".xaml.cs"));
        foreach (string required in new[]
        {
            "TogetherSectionPlanner.CreateWholeSong",
            "TogetherSectionPlanner.Split",
            "TogetherSectionPlanner.JoinWithNext",
            "TogetherSectionPlanner.MoveBoundary",
            "TogetherSectionPlanner.AssignOwner",
            "TogetherSectionPlanner.FinalizeTimeline",
            "ChooseParticipantClick"
        })
            Check(code.Contains(required, StringComparison.Ordinal),
                "The simple Together workflow is not wired to " + required);

        int createStart = code.IndexOf("private void CreateWorkspaceClick", StringComparison.Ordinal);
        int createEnd = code.IndexOf("private void OpenWorkspaceClick", createStart,
            StringComparison.Ordinal);
        Check(createStart >= 0 && createEnd > createStart,
            "Could not inspect the create-workspace handler.");
        string createHandler = code[createStart..createEnd];
        Check(!createHandler.Contains("ParseCuts", StringComparison.Ordinal),
            "New collaboration creation must not fall back to floor-number splitting.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src",
                    "ExtremeEditor.Wpf", "Dialogs", "TogetherWorkspaceWindow.xaml")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
