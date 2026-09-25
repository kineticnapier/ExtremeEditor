using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class OpenDirtyDocumentRegression
{
    public static void Run()
    {
        string source = File.ReadAllText(FindSourceFile("src/ExtremeEditor.Wpf/MainWindow.xaml.cs"));
        string body = ExtractMethodBody(source, "private async void ExecuteOpen(");
        if (string.IsNullOrEmpty(body))
            throw new InvalidOperationException("RED setup failure: MainWindow.ExecuteOpen was not found.");

        int discardGuard = body.IndexOf("if (!ConfirmDiscardCurrentChanges())", StringComparison.Ordinal);
        int dialogCreation = body.IndexOf("new OpenFileDialog", StringComparison.Ordinal);
        if (discardGuard < 0 || dialogCreation < 0 || discardGuard > dialogCreation)
        {
            throw new InvalidOperationException(
                "RED: ExecuteOpen must reject/discard-confirm a dirty editor session before opening the file dialog.");
        }
    }

    private static string FindSourceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"RED setup failure: unable to locate {relativePath}.");
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        int open = source.IndexOf('{', start);
        if (open < 0)
            return string.Empty;

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(open, i - open + 1);
        }

        return string.Empty;
    }
}
