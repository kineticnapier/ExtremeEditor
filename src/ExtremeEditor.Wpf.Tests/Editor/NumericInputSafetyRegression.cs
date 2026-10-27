using System.Globalization;
using System.IO;
using System.Reflection;

namespace ExtremeEditor.Wpf.Tests;

internal static class NumericInputSafetyRegression
{
    internal static void Run()
    {
        VerifySettingsFormattingRoundTrips();
        VerifyFiniteInputGuards();
    }

    private static void VerifySettingsFormattingRoundTrips()
    {
        MethodInfo format = typeof(MainWindow).GetMethod(
            "FormatSetting",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RED: FormatSetting is missing.");

        foreach (double expected in new[] { 123.456789, 0.0004, -17.123456789012345 })
        {
            string text = (string?)format.Invoke(null, [expected])
                ?? throw new InvalidOperationException("RED: FormatSetting returned null.");
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double actual) ||
                BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(expected))
            {
                throw new InvalidOperationException(
                    $"RED: settings text must round-trip without changing {expected:R}; got '{text}'.");
            }
        }
    }

    private static void VerifyFiniteInputGuards()
    {
        string dialog = ReadSource("src/ExtremeEditor.Wpf/Dialogs/InputDialog.cs");
        string properties = ReadSource(
            "src/ExtremeEditor.Wpf/MainWindow/MainWindow.EventPropertyEditor.cs");
        Require(dialog.Contains("double.IsFinite", StringComparison.Ordinal),
            "arbitrary-angle input must reject NaN and Infinity before editing floors.");
        Require(properties.Contains("double.IsFinite", StringComparison.Ordinal),
            "event scalar and vector inputs must reject non-finite JSON numbers.");
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
            throw new InvalidOperationException("RED: " + message);
    }
}
