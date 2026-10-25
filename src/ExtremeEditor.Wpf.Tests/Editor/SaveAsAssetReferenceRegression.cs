using System.IO;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class SaveAsAssetReferenceRegression
{
    public static void Run()
    {
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-SaveAsAssets-{Guid.NewGuid():N}");
        string sourceDirectory = Path.Combine(rootDirectory, "source");
        string targetDirectory = Path.Combine(rootDirectory, "target");
        string sourcePath = Path.Combine(sourceDirectory, "level.adofai");
        string targetPath = Path.Combine(targetDirectory, "saved.adofai");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetDirectory);

        string[] assets = ["song.ogg", "initial-bg.png", "event-bg.png", "decoration.png"];
        try
        {
            foreach (string asset in assets)
                File.WriteAllText(Path.Combine(sourceDirectory, asset), asset);
            File.WriteAllText(sourcePath, CreateFixture().ToJsonString());

            var session = new EditorSession(AdoFaiLoader.Load(sourcePath).Document);
            session.SaveAsync(targetPath).GetAwaiter().GetResult();

            JsonObject saved = JsonNode.Parse(File.ReadAllText(targetPath)) as JsonObject
                ?? throw new InvalidOperationException("Save As output is not a JSON object.");
            AssertResolves(targetDirectory, saved["settings"]?["songFilename"], "songFilename");
            AssertResolves(targetDirectory, saved["settings"]?["bgImage"], "initial bgImage");
            AssertResolves(targetDirectory, saved["actions"]?[0]?["bgImage"], "CustomBackground bgImage");
            AssertResolves(targetDirectory, saved["decorations"]?[0]?["decorationImage"], "decorationImage");

            string? runtimeSong = session.Document.ResolveSongPath();
            if (runtimeSong is null || !File.Exists(runtimeSong))
                throw new InvalidOperationException("Save As left the in-memory song reference relative to the old directory.");

            LevelAction background = session.Document.ActionStore.Actions.Single();
            AssertResolves(targetDirectory, background.PropertyOverrides?["bgImage"], "in-memory CustomBackground");
            AssertResolves(targetDirectory, session.Document.Decorations.Single().Properties["decorationImage"], "in-memory decoration");

            foreach (string asset in assets)
            {
                if (File.ReadAllText(Path.Combine(sourceDirectory, asset)) != asset)
                    throw new InvalidOperationException("Save As modified an existing source asset.");
            }

            VerifySameDirectoryKeepsReferences(sourcePath, sourceDirectory);
            VerifyAbsoluteReferenceIsUnchanged(sourceDirectory, targetDirectory);
            VerifyMissingReferenceDoesNotOverwrite(sourceDirectory, targetDirectory);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
                Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static void VerifySameDirectoryKeepsReferences(string sourcePath, string sourceDirectory)
    {
        string sameDirectoryPath = Path.Combine(sourceDirectory, "same-directory.adofai");
        var session = new EditorSession(AdoFaiLoader.Load(sourcePath).Document);
        session.SaveAsync(sameDirectoryPath).GetAwaiter().GetResult();
        JsonObject saved = JsonNode.Parse(File.ReadAllText(sameDirectoryPath)) as JsonObject
            ?? throw new InvalidOperationException("Same-directory Save As output is invalid.");
        if (!string.Equals(saved["settings"]?["songFilename"]?.GetValue<string>(), "song.ogg", StringComparison.Ordinal) ||
            !string.Equals(saved["settings"]?["bgImage"]?.GetValue<string>(), "initial-bg.png", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Same-directory Save As unnecessarily rewrote relative references.");
        }
    }

    private static void VerifyAbsoluteReferenceIsUnchanged(string sourceDirectory, string targetDirectory)
    {
        string sourcePath = Path.Combine(sourceDirectory, "absolute.adofai");
        string targetPath = Path.Combine(targetDirectory, "absolute.adofai");
        string absoluteAsset = Path.GetFullPath(Path.Combine(sourceDirectory, "initial-bg.png"));
        JsonObject fixture = CreateFixture();
        fixture["settings"]!["bgImage"] = absoluteAsset;
        File.WriteAllText(sourcePath, fixture.ToJsonString());

        var session = new EditorSession(AdoFaiLoader.Load(sourcePath).Document);
        session.SaveAsync(targetPath).GetAwaiter().GetResult();
        JsonObject saved = JsonNode.Parse(File.ReadAllText(targetPath)) as JsonObject
            ?? throw new InvalidOperationException("Absolute-reference Save As output is invalid.");
        if (!string.Equals(saved["settings"]?["bgImage"]?.GetValue<string>(), absoluteAsset, StringComparison.Ordinal))
            throw new InvalidOperationException("Save As rewrote an absolute asset reference.");
    }

    private static void VerifyMissingReferenceDoesNotOverwrite(string sourceDirectory, string targetDirectory)
    {
        string sourcePath = Path.Combine(sourceDirectory, "missing.adofai");
        string targetPath = Path.Combine(targetDirectory, "existing.adofai");
        JsonObject fixture = CreateFixture();
        fixture["settings"]!["songFilename"] = "missing-song.ogg";
        File.WriteAllText(sourcePath, fixture.ToJsonString());
        File.WriteAllText(targetPath, "keep-existing");

        var session = new EditorSession(AdoFaiLoader.Load(sourcePath).Document);
        session.InsertAngle(1, 45);
        try
        {
            session.SaveAsync(targetPath).GetAwaiter().GetResult();
            throw new InvalidOperationException("Save As accepted a missing relative asset reference.");
        }
        catch (FileNotFoundException)
        {
        }

        if (!string.Equals(File.ReadAllText(targetPath), "keep-existing", StringComparison.Ordinal) ||
            !session.IsDirty ||
            !string.Equals(session.Document.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Failed Save As modified the destination or session source path.");
        }
    }

    private static void AssertResolves(string directory, JsonNode? node, string label)
    {
        string value = node?.GetValue<string>()
            ?? throw new InvalidOperationException($"Save As omitted {label}.");
        string resolved = Path.IsPathRooted(value)
            ? value
            : Path.GetFullPath(Path.Combine(directory, value));
        if (!File.Exists(resolved))
            throw new InvalidOperationException($"RED: Save As broke {label}: {value}");
    }

    private static JsonObject CreateFixture() => new()
    {
        ["angleData"] = new JsonArray(0, 90),
        ["settings"] = new JsonObject
        {
            ["bpm"] = 120,
            ["songFilename"] = "song.ogg",
            ["bgImage"] = "initial-bg.png"
        },
        ["actions"] = new JsonArray
        {
            new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "CustomBackground",
                ["active"] = true,
                ["bgImage"] = "event-bg.png"
            }
        },
        ["decorations"] = new JsonArray
        {
            new JsonObject
            {
                ["floor"] = 1,
                ["eventType"] = "AddDecoration",
                ["decorationImage"] = "decoration.png"
            }
        }
    };
}
