using System.IO;

namespace ExtremeEditor.Wpf.Tests;

internal static class FocusedTestDispatchRegression
{
    internal static void Run()
    {
        string source = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory, "src", "ExtremeEditor.Wpf.Tests", "Program.cs"));
        int firstDispatch = source.IndexOf("if (string.Equals(", StringComparison.Ordinal);
        int fullSuite = source.IndexOf(
            "            FocusedTestDispatchRegression.Run();", StringComparison.Ordinal);

        if (firstDispatch < 0 || fullSuite <= firstDispatch ||
            source[..firstDispatch].Contains("Regression.Run();", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Focused WPF test modes must not execute unrelated regressions before dispatch.");
        }

        foreach (string name in new[]
        {
            nameof(SongSettingsAudioAsyncRegression),
            nameof(IncrementalActionPropertyEditRegression),
            nameof(LazyLevelMetadataRegression)
        })
        {
            string invocation = name + ".Run();";
            int call = source.IndexOf(invocation, StringComparison.Ordinal);
            if (call <= fullSuite ||
                source.IndexOf(invocation, call + invocation.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    "The full WPF suite must run " + name +
                    " exactly once, without leaking it into focused test modes.");
            }
        }
    }
}
