using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class DecorationModelFoundationRegression
{
    private static readonly JsonDocumentOptions TolerantJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static void Run()
    {
        DecorationContract contract = DecorationContract.Discover();
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-DecorationModel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            VerifyLoaderContract(contract, directory);
            VerifyFloorlessDecorationRoundTripAndStructureEdits(contract, directory);
            VerifyMalformedDecorationCompatibility(contract, directory);
            VerifyUneditedRoundTrip(contract, directory);
            VerifyInsertionAndUndoRedo(contract, directory);
            VerifyDeletionAndUndoRedo(contract, directory);
            VerifyCopyPasteAndUndoRedo(contract, directory);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyFloorlessDecorationRoundTripAndStructureEdits(
        DecorationContract contract,
        string directory)
    {
        string sourcePath = Path.Combine(directory, "floorless-source.adofai");
        string savedPath = Path.Combine(directory, "floorless-saved.adofai");
        File.WriteAllText(sourcePath, """
        {
          "angleData": [0, 90, 180, 270, 45,],
          "settings": { "bpm": 100 },
          "actions": [],
          "decorations": [
            { "floor": 2, "eventType": "AddDecoration", "tag": "floored" },
            {
              "eventType": "AddText",
              "decText": "global text",
              "relativeTo": "Global",
              "position": [12.5, 34.5],
              "customFutureProperty": { "nested": [1, 2, 3] }
            },
            { "floor": 4, "eventType": "AddObject", "tag": "after" },
          ],
        }
        """);

        JsonObject sourceRoot = ReadRoot(sourcePath);
        JsonArray sourceDecorations = sourceRoot["decorations"] as JsonArray
            ?? throw new InvalidOperationException("Floorless fixture has no decorations array.");
        LevelDocument document = AdoFaiLoader.Load(sourcePath).Document;
        IReadOnlyList<object> loaded = contract.GetDecorations(document);
        if (loaded.Count != 3)
        {
            throw new InvalidOperationException(
                "AdoFaiLoader discarded a decoration whose floor property is absent.");
        }

        object floorlessModel = loaded.Single(item => contract.GetSourceIndex(item) == 1);
        if (contract.GetFloor(floorlessModel) is not null)
            throw new InvalidOperationException("A missing decoration floor must not be represented as floor 0.");
        if (!string.Equals(contract.GetEventType(floorlessModel), "AddText", StringComparison.Ordinal))
            throw new InvalidOperationException("Floorless AddText did not preserve its event type.");
        JsonObject expectedProperties = NonCoreProperties((JsonObject)sourceDecorations[1]!.DeepClone());
        AssertJsonEquivalent(expectedProperties, contract.GetProperties(floorlessModel),
            "Floorless decoration unknown or nested properties were not preserved.");

        DecorationSnapshot originalFloorless = Snapshot(contract, document)
            .Single(item => item.SourceIndex == 1);
        DecorationSnapshot[] expectedLoaded = Snapshot(contract, document);
        AssertSnapshotsEqual(
            expectedLoaded,
            Snapshot(contract, AdoFaiLoader.LoadFlatAsync(sourcePath).GetAwaiter().GetResult().Document),
            "LoadFlatAsync discarded or changed a floorless decoration.");
        AssertSnapshotsEqual(
            expectedLoaded,
            Snapshot(contract, AdoFaiLoader.LoadAsync(sourcePath).GetAwaiter().GetResult().Document),
            "LoadAsync discarded or changed a floorless decoration.");

        var session = new EditorSession(document);
        session.InsertAngle(1, 30.0);
        AssertFloorlessUnchanged(originalFloorless, Snapshot(contract, session.Document),
            "Floor insertion changed a floorless decoration.");
        session.Undo();
        AssertFloorlessUnchanged(originalFloorless, Snapshot(contract, session.Document),
            "Undo insertion changed or removed a floorless decoration.");
        session.Redo();
        AssertFloorlessUnchanged(originalFloorless, Snapshot(contract, session.Document),
            "Redo insertion changed or removed a floorless decoration.");
        session.Undo();

        session.DeleteFloors([2]);
        AssertFloorlessUnchanged(originalFloorless, Snapshot(contract, session.Document),
            "Floor deletion changed or removed a floorless decoration.");
        session.Undo();
        AssertFloorlessUnchanged(originalFloorless, Snapshot(contract, session.Document),
            "Undo deletion changed or removed a floorless decoration.");
        session.Redo();
        AssertFloorlessUnchanged(originalFloorless, Snapshot(contract, session.Document),
            "Redo deletion changed or removed a floorless decoration.");

        var uneditedSession = new EditorSession(AdoFaiLoader.Load(sourcePath).Document);
        uneditedSession.SaveAsync(savedPath).GetAwaiter().GetResult();
        JsonArray savedDecorations = ReadRoot(savedPath)["decorations"] as JsonArray
            ?? throw new InvalidOperationException("Saved floorless fixture has no decorations array.");
        if (savedDecorations[1] is not JsonObject savedFloorless || savedFloorless.ContainsKey("floor"))
            throw new InvalidOperationException("Unedited save added a floor property to a floorless decoration.");
        AssertJsonEquivalent(sourceDecorations, savedDecorations,
            "Floorless decorations did not survive an unedited semantic round trip.");
    }

    private static void VerifyLoaderContract(DecorationContract contract, string directory)
    {
        string path = WriteFixture(directory, "loader.adofai");
        LevelDocument document = AdoFaiLoader.Load(path).Document;
        JsonArray source = ReadRoot(path)["decorations"] as JsonArray
            ?? throw new InvalidOperationException("Decoration fixture is missing its decorations array.");
        IReadOnlyList<object> decorations = contract.GetDecorations(document);

        if (decorations.Count != source.Count || contract.GetDecorationCount(document) != source.Count)
            throw new InvalidOperationException("AdoFaiLoader did not load every decoration into LevelDocument.");

        string[] expectedTypes = ["AddDecoration", "AddText", "AddParticle", "AddObject"];
        int[] expectedFloors = [1, 2, 3, 5];
        for (int i = 0; i < decorations.Count; i++)
        {
            object decoration = decorations[i];
            if (contract.GetFloor(decoration) != expectedFloors[i])
                throw new InvalidOperationException($"Decoration {i} did not preserve its floor.");
            if (!string.Equals(contract.GetEventType(decoration), expectedTypes[i], StringComparison.Ordinal))
                throw new InvalidOperationException($"Decoration {i} did not preserve its event type or ordering.");
            if (contract.GetSourceIndex(decoration) != i)
                throw new InvalidOperationException($"Decoration {i} does not retain decorations[] source identity {i}.");

            JsonObject expectedProperties = NonCoreProperties((JsonObject)source[i]!.DeepClone());
            if (!JsonNode.DeepEquals(expectedProperties, contract.GetProperties(decoration)))
            {
                throw new InvalidOperationException(
                    $"Decoration {i} did not preserve its opaque non-core JSON properties semantically.");
            }
        }

        IReadOnlyDictionary<string, int> typeCounts = contract.GetTypeCounts(document);
        foreach (string eventType in expectedTypes)
        {
            if (!typeCounts.TryGetValue(eventType, out int count) || count != 1)
                throw new InvalidOperationException($"DecorationTypeCounts is incorrect for {eventType}.");
        }

        DecorationSnapshot[] expected = Snapshot(contract, document);
        LevelDocument flat = AdoFaiLoader.LoadFlatAsync(path).GetAwaiter().GetResult().Document;
        AssertSnapshotsEqual(expected, Snapshot(contract, flat),
            "LoadFlatAsync did not preserve first-class decorations.");
        LevelDocument streaming = AdoFaiLoader.LoadAsync(path).GetAwaiter().GetResult().Document;
        AssertSnapshotsEqual(expected, Snapshot(contract, streaming),
            "LoadAsync did not preserve first-class decorations.");
    }

    private static void VerifyMalformedDecorationCompatibility(
        DecorationContract contract,
        string directory)
    {
        string path = Path.Combine(directory, "malformed-decorations.adofai");
        File.WriteAllText(path, """
        {
          "angleData": [0, 90,],
          "settings": { "bpm": 100 },
          "actions": [],
          "decorations": [
            42,
            { "eventType": "MissingFloor", "future": true },
            { "floor": "not-an-integer", "eventType": "BadFloor" },
            { "floor": "1", "eventType": 123, "future": { "nested": [1, 2] } },
          ],
        }
        """);

        foreach (LevelDocument document in new[]
        {
            AdoFaiLoader.Load(path).Document,
            AdoFaiLoader.LoadFlatAsync(path).GetAwaiter().GetResult().Document,
            AdoFaiLoader.LoadAsync(path).GetAwaiter().GetResult().Document
        })
        {
            IReadOnlyList<object> decorations = contract.GetDecorations(document);
            if (decorations.Count != 2 || contract.GetFloor(decorations[0]) is not null ||
                contract.GetSourceIndex(decorations[0]) != 1 ||
                !string.Equals(contract.GetEventType(decorations[0]), "MissingFloor", StringComparison.Ordinal) ||
                contract.GetFloor(decorations[1]) != 1 ||
                contract.GetSourceIndex(decorations[1]) != 3 ||
                !string.Equals(contract.GetEventType(decorations[1]), "123", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Only malformed explicit floors may be skipped; missing floors and later source identity must survive.");
            }
        }
    }

    private static void VerifyUneditedRoundTrip(DecorationContract contract, string directory)
    {
        string sourcePath = WriteFixture(directory, "round-trip-source.adofai");
        string savedPath = Path.Combine(directory, "round-trip-saved.adofai");
        JsonObject before = ReadRoot(sourcePath);
        LevelDocument document = AdoFaiLoader.Load(sourcePath).Document;

        // This assertion makes the test prove that SaveAsync is operating on a
        // first-class model rather than passing an unobserved source array through.
        _ = contract.GetDecorations(document);
        new EditorSession(document).SaveAsync(savedPath).GetAwaiter().GetResult();

        JsonObject after = ReadRoot(savedPath);
        AssertJsonEquivalent(before["decorations"], after["decorations"],
            "Unedited save changed decorations or their order.");
        AssertJsonEquivalent(before["actions"], after["actions"],
            "Decoration support changed the existing action round trip.");
    }

    private static void VerifyInsertionAndUndoRedo(DecorationContract contract, string directory)
    {
        string path = WriteFixture(directory, "insert.adofai");
        var session = new EditorSession(AdoFaiLoader.Load(path).Document);
        DecorationSnapshot[] original = Snapshot(contract, session.Document);

        session.InsertAngle(2, 45.0);
        DecorationSnapshot[] inserted = Snapshot(contract, session.Document);
        AssertFloors(inserted, [1, 2, 4, 6], "Floor insertion did not move decorations exactly like actions.");
        AssertSameIdentitiesAndCount(original, inserted, "Floor insertion duplicated or lost decorations.");
        AssertReference(inserted.Single(item => item.SourceIndex == 0), "startTile", new JsonArray(5, "ThisTile"));
        AssertReference(inserted.Single(item => item.SourceIndex == 3), "endTile", JsonValue.Create(7));

        session.Undo();
        AssertSnapshotsEqual(original, Snapshot(contract, session.Document),
            "Undo insertion did not restore decoration floors, properties, and source identity.");

        session.Redo();
        AssertSnapshotsEqual(inserted, Snapshot(contract, session.Document),
            "Redo insertion did not restore the same logical decorations.");
    }

    private static void VerifyDeletionAndUndoRedo(DecorationContract contract, string directory)
    {
        string path = WriteFixture(directory, "delete.adofai");
        var session = new EditorSession(AdoFaiLoader.Load(path).Document);
        DecorationSnapshot[] original = Snapshot(contract, session.Document);

        session.DeleteFloors([3]);
        DecorationSnapshot[] deleted = Snapshot(contract, session.Document);
        AssertFloors(deleted, [1, 2, 4], "Floor deletion did not remove and shift decorations correctly.");
        if (deleted.Any(item => item.SourceIndex == 2))
            throw new InvalidOperationException("A decoration attached to a deleted floor was not removed from LevelDocument.");
        AssertReference(deleted.Single(item => item.SourceIndex == 0), "startTile", new JsonArray(3, "ThisTile"));
        AssertReference(deleted.Single(item => item.SourceIndex == 3), "endTile", JsonValue.Create(5));

        session.Undo();
        AssertSnapshotsEqual(original, Snapshot(contract, session.Document),
            "Undo deletion did not restore decoration count, floors, properties, and source identity.");

        session.Redo();
        AssertSnapshotsEqual(deleted, Snapshot(contract, session.Document),
            "Redo deletion duplicated, lost, or changed decorations.");
    }

    private static void VerifyCopyPasteAndUndoRedo(DecorationContract contract, string directory)
    {
        string path = WriteFixture(directory, "copy-paste.adofai");
        var session = new EditorSession(AdoFaiLoader.Load(path).Document);
        DecorationSnapshot[] original = Snapshot(contract, session.Document);
        DecorationSnapshot copied = original.Single(item => item.Floor == 2);

        session.CopyFloors([2]);
        session.PasteFloors(5);
        DecorationSnapshot[] pasted = Snapshot(contract, session.Document);
        if (pasted.Length != original.Length + 1)
            throw new InvalidOperationException("Pasted decorations did not appear immediately in LevelDocument.");

        DecorationSnapshot created = pasted.Single(item =>
            item.Floor == 6 && string.Equals(item.EventType, copied.EventType, StringComparison.Ordinal));
        if (created.SourceIndex == copied.SourceIndex ||
            original.Any(item => item.SourceIndex == created.SourceIndex))
        {
            throw new InvalidOperationException("A pasted decoration reused an existing source identity.");
        }
        AssertJsonEquivalent(copied.Properties, created.Properties,
            "Copy/paste discarded arbitrary decoration properties.");

        session.Undo();
        AssertSnapshotsEqual(original, Snapshot(contract, session.Document),
            "Undo paste did not remove the pasted decoration cleanly.");

        session.Redo();
        DecorationSnapshot[] redone = Snapshot(contract, session.Document);
        DecorationSnapshot recreated = redone.Single(item => item.SourceIndex == created.SourceIndex);
        if (recreated.Floor != created.Floor ||
            !string.Equals(recreated.EventType, created.EventType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Redo paste did not preserve the pasted decoration identity and location.");
        }
        AssertJsonEquivalent(created.Properties, recreated.Properties,
            "Redo paste did not preserve the pasted decoration properties.");
    }

    private static string WriteFixture(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, """
        {
          // The production loader must retain its tolerant JSON behavior.
          "angleData": [0, 90, 180, 270, 45, 135, 225,],
          "settings": { "bpm": 120, "offset": 0, "pitch": 100, },
          "actions": [
            { "floor": 1, "eventType": "Twirl", "active": true, "futureAction": { "keep": true } },
            { "floor": 5, "eventType": "SetSpeed", "active": true, "speedType": "Multiplier", "bpmMultiplier": 2.0 },
          ],
          "decorations": [
            {
              "floor": 1,
              "eventType": "AddDecoration",
              "decorationImage": "sprite.png",
              "relativeTo": "Tile",
              "color": "ff8040",
              "opacity": 73.5,
              "depth": -12,
              "parallax": [80, 120],
              "maskingType": "Mask",
              "blendMode": "Screen",
              "startTile": [4, "ThisTile"],
              "futureScalar": "preserve-me",
              "futureObject": { "nested": { "enabled": true }, "number": 12.25 },
              "futureArray": [1, "two", false, { "deep": [3, 4] }],
              "tag": "first",
            },
            {
              "floor": 2,
              "eventType": "AddText",
              "decText": "hello",
              "relativeTo": "Global",
              "color": "abcdef",
              "opacity": 100,
              "depth": 2,
              "parallax": [100, 100],
              "tag": "copy-me",
              "futureTextMode": { "alignment": "Future" },
            },
            {
              "floor": 3,
              "eventType": "AddParticle",
              "relativeTo": "RedPlanet",
              "color": "00ff00",
              "opacity": 25,
              "depth": 5,
              "parallax": [90, 110],
              "maskingType": "VisibleInsideMask",
              "blendMode": "Add",
              "tag": "deleted",
              "futureParticle": ["spark", { "rate": 7 }],
            },
            {
              "floor": 5,
              "eventType": "AddObject",
              "relativeTo": "Camera",
              "color": "112233",
              "opacity": 88,
              "depth": 9,
              "parallax": [70, 60],
              "maskingType": "None",
              "blendMode": "Multiply",
              "endTile": 6,
              "tag": "last",
              "futureObjectKind": { "shape": "unknown", "arguments": [1, 2, 3] },
            },
          ],
        }
        """);
        return path;
    }

    private static JsonObject ReadRoot(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: TolerantJsonOptions) as JsonObject
        ?? throw new InvalidOperationException($"{Path.GetFileName(path)} root is not a JSON object.");

    private static JsonObject NonCoreProperties(JsonObject source)
    {
        source.Remove("floor");
        source.Remove("eventType");
        return source;
    }

    private static DecorationSnapshot[] Snapshot(DecorationContract contract, LevelDocument document) =>
        contract.GetDecorations(document)
            .Select(item => new DecorationSnapshot(
                contract.GetFloor(item),
                contract.GetEventType(item),
                contract.GetSourceIndex(item),
                (JsonObject)contract.GetProperties(item).DeepClone()))
            .ToArray();

    private static void AssertFloors(
        IReadOnlyList<DecorationSnapshot> actual,
        IReadOnlyList<int> expected,
        string message)
    {
        if (!actual.Select(item => item.Floor).SequenceEqual(expected.Select(floor => (int?)floor)))
            throw new InvalidOperationException(message);
    }

    private static void AssertFloorlessUnchanged(
        DecorationSnapshot expected,
        IReadOnlyList<DecorationSnapshot> actual,
        string message)
    {
        DecorationSnapshot? current = actual.SingleOrDefault(item => item.SourceIndex == expected.SourceIndex);
        if (current is null || current.Floor is not null ||
            !string.Equals(current.EventType, expected.EventType, StringComparison.Ordinal) ||
            !JsonSemanticallyEquals(current.Properties, expected.Properties))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertSameIdentitiesAndCount(
        IReadOnlyList<DecorationSnapshot> before,
        IReadOnlyList<DecorationSnapshot> after,
        string message)
    {
        if (before.Count != after.Count ||
            !before.Select(item => item.SourceIndex).Order().SequenceEqual(
                after.Select(item => item.SourceIndex).Order()))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertSnapshotsEqual(
        IReadOnlyList<DecorationSnapshot> expected,
        IReadOnlyList<DecorationSnapshot> actual,
        string message)
    {
        if (expected.Count != actual.Count)
            throw new InvalidOperationException(message);

        for (int i = 0; i < expected.Count; i++)
        {
            DecorationSnapshot left = expected[i];
            DecorationSnapshot right = actual[i];
            if (left.Floor != right.Floor || left.SourceIndex != right.SourceIndex ||
                !string.Equals(left.EventType, right.EventType, StringComparison.Ordinal) ||
                !JsonNode.DeepEquals(left.Properties, right.Properties))
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private static void AssertReference(DecorationSnapshot decoration, string name, JsonNode? expected)
    {
        if (!JsonNode.DeepEquals(decoration.Properties[name], expected))
        {
            throw new InvalidOperationException(
                $"Decoration {name} reference did not follow the existing structure-edit transform.");
        }
    }

    private static void AssertJsonEquivalent(JsonNode? expected, JsonNode? actual, string message)
    {
        if (!JsonSemanticallyEquals(expected, actual))
            throw new InvalidOperationException(message);
    }

    private static bool JsonSemanticallyEquals(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            if (leftObject.Count != rightObject.Count)
                return false;
            return leftObject.All(pair => rightObject.TryGetPropertyValue(pair.Key, out JsonNode? value) &&
                JsonSemanticallyEquals(pair.Value, value));
        }
        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            return leftArray.Count == rightArray.Count &&
                leftArray.Zip(rightArray).All(pair => JsonSemanticallyEquals(pair.First, pair.Second));
        }
        if (left is not JsonValue || right is not JsonValue)
            return false;

        using JsonDocument leftDocument = JsonDocument.Parse(left.ToJsonString());
        using JsonDocument rightDocument = JsonDocument.Parse(right.ToJsonString());
        JsonElement leftElement = leftDocument.RootElement;
        JsonElement rightElement = rightDocument.RootElement;
        if (leftElement.ValueKind != rightElement.ValueKind)
            return false;
        if (leftElement.ValueKind == JsonValueKind.Number)
        {
            if (leftElement.TryGetDecimal(out decimal leftDecimal) &&
                rightElement.TryGetDecimal(out decimal rightDecimal))
            {
                return leftDecimal == rightDecimal;
            }
            return leftElement.GetDouble().Equals(rightElement.GetDouble());
        }
        return leftElement.ValueKind switch
        {
            JsonValueKind.String => string.Equals(
                leftElement.GetString(), rightElement.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => string.Equals(
                leftElement.GetRawText(), rightElement.GetRawText(), StringComparison.Ordinal)
        };
    }

    private sealed record DecorationSnapshot(
        int? Floor,
        string EventType,
        int SourceIndex,
        JsonObject Properties);

    private sealed class DecorationContract
    {
        private readonly Type _decorationType;
        private readonly PropertyInfo _decorations;
        private readonly PropertyInfo _decorationCount;
        private readonly PropertyInfo _decorationTypeCounts;
        private readonly PropertyInfo _floor;
        private readonly PropertyInfo _eventType;
        private readonly PropertyInfo _sourceIndex;
        private readonly PropertyInfo _properties;

        private DecorationContract(
            Type decorationType,
            PropertyInfo decorations,
            PropertyInfo decorationCount,
            PropertyInfo decorationTypeCounts,
            PropertyInfo floor,
            PropertyInfo eventType,
            PropertyInfo sourceIndex,
            PropertyInfo properties)
        {
            _decorationType = decorationType;
            _decorations = decorations;
            _decorationCount = decorationCount;
            _decorationTypeCounts = decorationTypeCounts;
            _floor = floor;
            _eventType = eventType;
            _sourceIndex = sourceIndex;
            _properties = properties;
        }

        public static DecorationContract Discover()
        {
            Type documentType = typeof(LevelDocument);
            Assembly coreAssembly = documentType.Assembly;
            Type? decorationType = coreAssembly.GetType("ExtremeEditor.Core.LevelDecoration");
            PropertyInfo? decorations = documentType.GetProperty("Decorations", BindingFlags.Instance | BindingFlags.Public);
            if (decorationType is null || decorations is null ||
                !typeof(IEnumerable).IsAssignableFrom(decorations.PropertyType))
            {
                throw new InvalidOperationException("LevelDocument does not expose first-class decorations.");
            }

            PropertyInfo decorationCount = RequireProperty(documentType, "DecorationCount", typeof(int));
            PropertyInfo decorationTypeCounts = documentType.GetProperty(
                "DecorationTypeCounts", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException(
                    "LevelDocument must expose observable decoration type counts.");
            PropertyInfo floor = decorationType.GetProperty("Floor", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("LevelDecoration must expose Floor.");
            if (floor.PropertyType != typeof(int) && floor.PropertyType != typeof(int?))
                throw new InvalidOperationException("LevelDecoration.Floor must be Int32 or Nullable<Int32>.");
            PropertyInfo eventType = RequireProperty(decorationType, "EventType", typeof(string));
            PropertyInfo sourceIndex = RequireProperty(decorationType, "SourceIndex", typeof(int));
            PropertyInfo? properties = decorationType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(property => property.PropertyType == typeof(JsonObject) &&
                    property.Name is "Properties" or "PropertyBag" or "PropertyOverrides");
            if (properties is null)
            {
                throw new InvalidOperationException(
                    "LevelDecoration must expose an opaque JsonObject property bag for non-core fields.");
            }

            MethodInfo? replace = documentType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "ReplaceDecorations" && method.GetParameters().Length == 1);
            if (replace is null)
            {
                throw new InvalidOperationException(
                    "LevelDocument must support replacing its first-class decorations.");
            }

            return new DecorationContract(
                decorationType,
                decorations,
                decorationCount,
                decorationTypeCounts,
                floor,
                eventType,
                sourceIndex,
                properties);
        }

        public IReadOnlyList<object> GetDecorations(LevelDocument document)
        {
            if (_decorations.GetValue(document) is not IEnumerable values)
                throw new InvalidOperationException("LevelDocument.Decorations returned no collection.");

            object[] result = values.Cast<object>().ToArray();
            if (result.Any(value => !_decorationType.IsInstanceOfType(value)))
                throw new InvalidOperationException("LevelDocument.Decorations contains a non-LevelDecoration value.");
            return result;
        }

        public int GetDecorationCount(LevelDocument document) =>
            (int)(_decorationCount.GetValue(document)
                ?? throw new InvalidOperationException("LevelDocument.DecorationCount returned null."));

        public IReadOnlyDictionary<string, int> GetTypeCounts(LevelDocument document)
        {
            object? value = _decorationTypeCounts.GetValue(document);
            if (value is IReadOnlyDictionary<string, int> counts)
                return counts;
            if (value is IDictionary<string, int> dictionary)
                return new Dictionary<string, int>(dictionary, StringComparer.Ordinal);
            throw new InvalidOperationException("LevelDocument.DecorationTypeCounts has an unusable type.");
        }

        public int? GetFloor(object decoration)
        {
            object? value = _floor.GetValue(decoration);
            return value is null ? null : (int)value;
        }

        public string GetEventType(object decoration) =>
            (string)(_eventType.GetValue(decoration)
                ?? throw new InvalidOperationException("LevelDecoration.EventType returned null."));

        public int GetSourceIndex(object decoration) =>
            (int)(_sourceIndex.GetValue(decoration)
                ?? throw new InvalidOperationException("LevelDecoration.SourceIndex returned null."));

        public JsonObject GetProperties(object decoration) =>
            (JsonObject)(_properties.GetValue(decoration)
                ?? throw new InvalidOperationException("LevelDecoration property bag returned null."));

        private static PropertyInfo RequireProperty(Type owner, string name, Type expectedType)
        {
            PropertyInfo property = owner.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"{owner.Name} must expose {name}.");
            if (property.PropertyType != expectedType)
                throw new InvalidOperationException($"{owner.Name}.{name} must be {expectedType.Name}.");
            return property;
        }
    }
}
