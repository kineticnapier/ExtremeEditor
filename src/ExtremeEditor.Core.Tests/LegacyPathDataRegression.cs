using ExtremeEditor.Core;

namespace ExtremeEditor.Core.Tests;

internal static class LegacyPathDataRegression
{
    internal static void Run()
    {
        string pathOnly = Write("""
            { "pathData": "R!5", "settings": { "bpm": 100 }, "actions": [] }
            """);
        string both = Write("""
            { "angleData": [45, 90], "pathData": "RRR", "settings": { "bpm": 100 }, "actions": [] }
            """);
        string unknown = Write("""
            { "pathData": "R?", "settings": { "bpm": 100 }, "actions": [] }
            """);
        string relatives = Write("""
            { "pathData": "R5678", "settings": { "bpm": 100 }, "actions": [] }
            """);
        string empty = Write("""
            { "pathData": "", "settings": { "bpm": 100 }, "actions": [] }
            """);
        string bom = Write("""
            { "pathData": "REJW", "settings": { "bpm": 100 }, "actions": [] }
            """, new System.Text.UTF8Encoding(true));
        try
        {
            AssertAngles(AdoFaiLoader.Load(pathOnly).Document.Angles, 0, 999, 351);
            AssertAngles(AdoFaiLoader.LoadAsync(pathOnly).GetAwaiter().GetResult().Document.Angles, 0, 999, 351);
            AssertAngles(AdoFaiLoader.LoadFlatAsync(pathOnly).GetAwaiter().GetResult().Document.Angles, 0, 999, 351);
            AssertAngles(AdoFaiLoader.Load(both).Document.Angles, 45, 90);
            AssertAngles(AdoFaiLoader.Load(relatives).Document.Angles, 0, 72, 0, 360.0 / 7.0, 0);
            AssertAngles(AdoFaiLoader.Load(empty).Document.Angles);
            AssertAngles(AdoFaiLoader.LoadFlatAsync(bom).GetAwaiter().GetResult().Document.Angles, 0, 45, 30, 165);

            try
            {
                _ = AdoFaiLoader.Load(unknown);
                throw new InvalidOperationException("Unknown pathData character was silently accepted.");
            }
            catch (InvalidDataException error) when (error.Message.Contains("?", StringComparison.Ordinal))
            {
            }
        }
        finally
        {
            File.Delete(pathOnly);
            File.Delete(both);
            File.Delete(unknown);
            File.Delete(relatives);
            File.Delete(empty);
            File.Delete(bom);
        }
    }

    private static string Write(string json, System.Text.Encoding? encoding = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-pathData-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, json, encoding ?? new System.Text.UTF8Encoding(false));
        return path;
    }

    private static void AssertAngles(double[] actual, params double[] expected)
    {
        if (actual.Length != expected.Length || actual.Where((value, index) => Math.Abs(value - expected[index]) > 1e-9).Any())
            throw new InvalidOperationException($"pathData angles: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");
    }
}
