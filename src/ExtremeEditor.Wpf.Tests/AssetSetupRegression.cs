using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class AssetSetupRegression
{
    public static void Run()
    {
        Assembly wpfAssembly = typeof(MainWindow).Assembly;
        if (wpfAssembly.GetReferencedAssemblies().Any(
                name => string.Equals(name.Name, "ExtremeEditor.AssetExtractor", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "ExtremeEditor.Wpf must not reference ExtremeEditor.AssetExtractor; setup must cross a process boundary.");
        }

        Type serviceType = wpfAssembly.GetType("ExtremeEditor.Wpf.AssetSetupService")
            ?? throw new InvalidOperationException("AssetSetupService does not exist yet.");

        MethodInfo inspectMethod = serviceType.GetMethod(
            "InspectCache",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException("AssetSetupService.InspectCache(string) is missing.");

        MethodInfo startInfoMethod = serviceType.GetMethod(
            "CreateExtractorStartInfo",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(string), typeof(string)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "AssetSetupService.CreateExtractorStartInfo(string extractorPath, string cacheRoot, string adofaiRoot) is missing.");

        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-Wpf-AssetSetup-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(cacheRoot);
            AssertReady(inspectMethod, cacheRoot, expected: false, "missing manifest/cache must be reported as not ready");

            WriteMinimalCanonicalCache(cacheRoot, formatVersion: 1);
            AssertReady(inspectMethod, cacheRoot, expected: true, "canonical formatVersion=1 cache must be accepted");

            WriteMinimalCanonicalCache(cacheRoot, formatVersion: 2);
            AssertReady(inspectMethod, cacheRoot, expected: false, "unknown manifest formatVersion must be rejected");

            string extractorPath = Path.Combine(cacheRoot, "ExtremeEditor.AssetExtractor.exe");
            string gameRoot = Path.Combine(cacheRoot, "ADOFAI");
            object? result = startInfoMethod.Invoke(null, [extractorPath, cacheRoot, gameRoot]);
            if (result is not ProcessStartInfo startInfo)
                throw new InvalidOperationException("CreateExtractorStartInfo must return ProcessStartInfo.");

            if (!string.Equals(startInfo.FileName, extractorPath, StringComparison.Ordinal))
                throw new InvalidOperationException("Extractor ProcessStartInfo must launch the supplied standalone executable.");
            if (startInfo.UseShellExecute)
                throw new InvalidOperationException("Asset setup must use UseShellExecute=false for deterministic process execution.");
            if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
                throw new InvalidOperationException("Asset setup must capture extractor stdout/stderr for the WPF status UI.");

            string[] arguments = startInfo.ArgumentList.ToArray();
            AssertArgumentPair(arguments, "--output", cacheRoot);
            AssertArgumentPair(arguments, "--adofai", gameRoot);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                try
                {
                    Directory.Delete(cacheRoot, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    private static void AssertReady(
        MethodInfo inspectMethod,
        string cacheRoot,
        bool expected,
        string message)
    {
        object? status = inspectMethod.Invoke(null, [cacheRoot]);
        if (status is null)
            throw new InvalidOperationException("InspectCache returned null.");

        PropertyInfo property = status.GetType().GetProperty("IsReady", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Asset cache status must expose a public IsReady property.");

        if (property.GetValue(status) is not bool actual || actual != expected)
            throw new InvalidOperationException(message);
    }

    private static void WriteMinimalCanonicalCache(string cacheRoot, int formatVersion)
    {
        string floorDirectory = Path.Combine(cacheRoot, "floor-mesh");
        string iconDirectory = Path.Combine(cacheRoot, "icons");
        string floorIconDirectory = Path.Combine(iconDirectory, "floors");
        string outlineDirectory = Path.Combine(iconDirectory, "outlines");
        string eventDirectory = Path.Combine(iconDirectory, "events");
        string categoryDirectory = Path.Combine(iconDirectory, "categories");
        string hitSoundDirectory = Path.Combine(cacheRoot, "hitsounds");

        Directory.CreateDirectory(floorDirectory);
        Directory.CreateDirectory(floorIconDirectory);
        Directory.CreateDirectory(outlineDirectory);
        Directory.CreateDirectory(eventDirectory);
        Directory.CreateDirectory(categoryDirectory);
        Directory.CreateDirectory(hitSoundDirectory);

        foreach (string name in new[] { "tile.png", "perlin.png", "ramp.png", "glow.png" })
            File.WriteAllBytes(Path.Combine(floorDirectory, name), [1]);

        File.WriteAllBytes(Path.Combine(floorIconDirectory, "Rabbit.png"), [1]);
        File.WriteAllBytes(Path.Combine(outlineDirectory, "Rabbit.png"), [1]);
        File.WriteAllBytes(Path.Combine(eventDirectory, "SetSpeed.png"), [1]);
        File.WriteAllBytes(Path.Combine(categoryDirectory, "Gameplay.png"), [1]);
        File.WriteAllBytes(Path.Combine(hitSoundDirectory, "sndKick.wav"), [1]);

        var manifest = new
        {
            formatVersion,
            gameVersion = "test-game-version",
            sourceFingerprint = "test-source-fingerprint",
            floorIcons = 1,
            outlineIcons = 1,
            eventIcons = 1,
            categoryIcons = 1,
            hitSounds = 1
        };

        File.WriteAllText(
            Path.Combine(cacheRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static void AssertArgumentPair(string[] arguments, string option, string expectedValue)
    {
        int index = Array.IndexOf(arguments, option);
        if (index < 0 || index + 1 >= arguments.Length ||
            !string.Equals(arguments[index + 1], expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Extractor ProcessStartInfo must contain '{option} {expectedValue}'.");
        }
    }
}
