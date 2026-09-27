using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class StaticDecorationRegression
{
    private const string MissingSnapshotContract =
        "NativeLevelSnapshot does not expose static AddDecoration instances.";

    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-StaticDecoration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            string subdirectory = Path.Combine(directory, "subdir");
            Directory.CreateDirectory(subdirectory);
            string unicodeImagePath = Path.Combine(subdirectory, "装飾.png");
            string globalImagePath = Path.Combine(directory, "global.png");
            WriteOnePixelPng(unicodeImagePath);
            WriteOnePixelPng(globalImagePath);

            string levelPath = Path.Combine(directory, "static-decoration.adofai");
            File.WriteAllText(levelPath, """
            {
              "angleData": [0, 90, 180],
              "settings": { "bpm": 100 },
              "actions": [],
              "decorations": [
                {
                  "floor": 1,
                  "eventType": "AddDecoration",
                  "decorationImage": "subdir/装飾.png",
                  "position": [2.5, -3.25],
                  "pivotOffset": [0.25, -0.5],
                  "rotation": 30,
                  "scale": [-125, 80],
                  "color": "12AB34",
                  "opacity": 65,
                  "visible": true,
                  "depth": -7,
                  "relativeTo": "Tile"
                },
                {
                  "floor": 2,
                  "eventType": "AddDecoration",
                  "decorationImage": "global.png",
                  "position": [-4, 6],
                  "pivotOffset": [1.5, 2],
                  "rotation": -45,
                  "scale": [50, 175],
                  "color": "FEDCBA",
                  "opacity": 100,
                  "visible": true,
                  "depth": 12,
                  "relativeTo": "Global"
                },
                {
                  "floor": 0,
                  "eventType": "AddDecoration",
                  "decorationImage": "global.png",
                  "position": [99, 99],
                  "pivotOffset": [0, 0],
                  "rotation": 0,
                  "scale": [100, 100],
                  "color": "FFFFFF",
                  "opacity": 100,
                  "visible": false,
                  "depth": 0,
                  "relativeTo": "Global"
                },
                {
                  "floor": 1,
                  "eventType": "AddDecoration",
                  "decorationImage": "missing.png",
                  "position": [8, 9],
                  "pivotOffset": [0, 0],
                  "rotation": 0,
                  "scale": [100, 100],
                  "color": "FFFFFF",
                  "opacity": 100,
                  "visible": true,
                  "depth": 0,
                  "relativeTo": "Tile"
                },
                {
                  "eventType": "AddDecoration",
                  "decorationImage": "global.png",
                  "position": [7.5, -8.5],
                  "pivotOffset": [-1, 1],
                  "rotation": 90,
                  "scale": [100, 100],
                  "color": "336699",
                  "opacity": 25,
                  "visible": true,
                  "depth": 3,
                  "relativeTo": "Global"
                },
                {
                  "floor": 2,
                  "eventType": "AddDecoration",
                  "decorationImage": "",
                  "position": [1, 1],
                  "pivotOffset": [0, 0],
                  "rotation": 0,
                  "scale": [100, 100],
                  "color": "FFFFFF",
                  "opacity": 100,
                  "visible": true,
                  "depth": 0,
                  "relativeTo": "Global"
                }
              ]
            }
            """);

            LevelDocument level = AdoFaiLoader.Load(levelPath).Document;
            if (level.Decorations.Count != 6 ||
                level.Decorations.Any(static decoration =>
                    !string.Equals(decoration.EventType, "AddDecoration", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Static AddDecoration fixture was not preserved by AdoFaiLoader.");
            }

            LevelDecoration floorless = level.Decorations.Single(decoration => decoration.SourceIndex == 4);
            if (floorless.Floor is not null)
                throw new InvalidOperationException("Floorless Global AddDecoration acquired a floor.");

            // Building must remain successful even when one image is missing or empty.
            NativeLevelSnapshot snapshot = NativeLevelSnapshotBuilder.Build(level);
            SnapshotContract contract = SnapshotContract.Discover(snapshot);

            IReadOnlyList<object> instances = contract.GetInstances(snapshot);
            IReadOnlyList<object> assets = contract.GetAssets(snapshot);

            object tile = contract.FindBySourceIndex(instances, 0);
            object global = contract.FindBySourceIndex(instances, 1);
            object floorlessGlobal = contract.FindBySourceIndex(instances, 4);
            contract.AssertInvisibleIsNotDrawable(instances, 2);

            contract.AssertInstance(
                tile,
                expectedAssetPath: unicodeImagePath,
                expectedPosition: (2.5, -3.25),
                expectedPivot: (0.25, -0.5),
                expectedRotationDegrees: 30,
                expectedScale: (-1.25, 0.8),
                expectedColor: "12AB34",
                expectedOpacity: 0.65,
                expectedDepth: -7,
                expectedRelativeTo: "Tile",
                assets);
            contract.AssertInstance(
                global,
                expectedAssetPath: globalImagePath,
                expectedPosition: (-4, 6),
                expectedPivot: (1.5, 2),
                expectedRotationDegrees: -45,
                expectedScale: (0.5, 1.75),
                expectedColor: "FEDCBA",
                expectedOpacity: 1,
                expectedDepth: 12,
                expectedRelativeTo: "Global",
                assets);
            contract.AssertInstance(
                floorlessGlobal,
                expectedAssetPath: globalImagePath,
                expectedPosition: (7.5, -8.5),
                expectedPivot: (-1, 1),
                expectedRotationDegrees: 90,
                expectedScale: (1, 1),
                expectedColor: "336699",
                expectedOpacity: 0.25,
                expectedDepth: 3,
                expectedRelativeTo: "Global",
                assets);

            if (assets.Any(asset =>
                    string.Equals(contract.GetAssetPath(asset), IconAssetPath("missing.png"), StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(contract.GetAssetPath(asset))))
            {
                throw new InvalidOperationException(
                    "Missing or empty decorationImage must not create an invalid native asset.");
            }

            string IconAssetPath(string filename) => Path.GetFullPath(Path.Combine(directory, filename));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteOnePixelPng(string path)
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/l1uHOwAAAABJRU5ErkJggg==");
        File.WriteAllBytes(path, png);
    }

    private sealed class SnapshotContract
    {
        private readonly PropertyInfo _instancesProperty;
        private readonly PropertyInfo _assetsProperty;

        private SnapshotContract(PropertyInfo instancesProperty, PropertyInfo assetsProperty)
        {
            _instancesProperty = instancesProperty;
            _assetsProperty = assetsProperty;
        }

        public static SnapshotContract Discover(NativeLevelSnapshot snapshot)
        {
            Type type = snapshot.GetType();
            PropertyInfo? instances = FindProperty(
                type,
                "StaticDecorations",
                "DecorationInstances",
                "Decorations");
            if (instances is null || !typeof(IEnumerable).IsAssignableFrom(instances.PropertyType))
                throw new InvalidOperationException(MissingSnapshotContract);

            PropertyInfo? assets = FindProperty(
                type,
                "DecorationAssets",
                "StaticDecorationAssets");
            if (assets is null || !typeof(IEnumerable).IsAssignableFrom(assets.PropertyType))
            {
                throw new InvalidOperationException(
                    "NativeLevelSnapshot does not expose resolved static decoration assets.");
            }

            return new SnapshotContract(instances, assets);
        }

        public IReadOnlyList<object> GetInstances(NativeLevelSnapshot snapshot) =>
            Enumerate(_instancesProperty.GetValue(snapshot));

        public IReadOnlyList<object> GetAssets(NativeLevelSnapshot snapshot) =>
            Enumerate(_assetsProperty.GetValue(snapshot));

        public object FindBySourceIndex(IReadOnlyList<object> instances, int sourceIndex) =>
            instances.SingleOrDefault(instance => ReadInt(instance, "SourceIndex") == sourceIndex)
            ?? throw new InvalidOperationException(
                $"Static AddDecoration source index {sourceIndex} is missing from the native snapshot.");

        public void AssertInvisibleIsNotDrawable(IReadOnlyList<object> instances, int sourceIndex)
        {
            object? instance = instances.SingleOrDefault(candidate => ReadInt(candidate, "SourceIndex") == sourceIndex);
            if (instance is null)
                return;

            object? visible = TryRead(instance, "Visible", "IsVisible");
            if (visible is not bool isVisible || isVisible)
            {
                throw new InvalidOperationException(
                    "visible:false AddDecoration must be omitted or marked non-visible in the native snapshot.");
            }
        }

        public void AssertInstance(
            object instance,
            string expectedAssetPath,
            (double X, double Y) expectedPosition,
            (double X, double Y) expectedPivot,
            double expectedRotationDegrees,
            (double X, double Y) expectedScale,
            string expectedColor,
            double expectedOpacity,
            int expectedDepth,
            string expectedRelativeTo,
            IReadOnlyList<object> assets)
        {
            int assetId = ReadInt(instance, "AssetId", "DecorationAssetId");
            if ((uint)assetId >= (uint)assets.Count)
                throw new InvalidOperationException("Static decoration asset id is out of range.");
            string actualPath = GetAssetPath(assets[assetId]);
            if (!string.Equals(actualPath, Path.GetFullPath(expectedAssetPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("decorationImage was not resolved relative to LevelDocument.SourcePath.");

            AssertPair(instance, expectedPosition, "Position", "X", "Y", "PositionX", "PositionY");
            AssertPair(instance, expectedPivot, "Pivot", "PivotX", "PivotY", "PivotOffsetX", "PivotOffsetY");
            AssertRotation(instance, expectedRotationDegrees);
            AssertPair(instance, expectedScale, "Scale", "ScaleX", "ScaleY", "ScaleX", "ScaleY");
            AssertNumber(instance, expectedOpacity, "Opacity");

            if (ReadInt(instance, "Depth") != expectedDepth)
                throw new InvalidOperationException("Static decoration depth was not preserved.");
            string relativeTo = ReadString(instance, "RelativeTo", "RelativeMode");
            if (!string.Equals(relativeTo, expectedRelativeTo, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Static decoration relativeTo mode was not preserved.");
            AssertColor(instance, expectedColor);
        }

        public string GetAssetPath(object asset) =>
            Path.GetFullPath(ReadString(asset, "ImagePath", "Path"));

        private static void AssertPair(
            object value,
            (double X, double Y) expected,
            string vectorName,
            string vectorX,
            string vectorY,
            string flatX,
            string flatY)
        {
            object? vector = TryRead(value, vectorName);
            if (vector is not null)
            {
                AssertNearly(ReadNumber(vector, vectorX), expected.X, vectorName + ".X");
                AssertNearly(ReadNumber(vector, vectorY), expected.Y, vectorName + ".Y");
                return;
            }

            AssertNearly(ReadNumber(value, flatX), expected.X, flatX);
            AssertNearly(ReadNumber(value, flatY), expected.Y, flatY);
        }

        private static void AssertNumber(object value, double expected, params string[] names) =>
            AssertNearly(ReadNumber(value, names), expected, names[0]);

        private static void AssertRotation(object value, double expectedDegrees)
        {
            object? degrees = TryRead(value, "RotationDegrees");
            if (degrees is not null)
            {
                AssertNearly(Convert.ToDouble(degrees, CultureInfo.InvariantCulture), expectedDegrees, "RotationDegrees");
                return;
            }

            double expectedRadians = expectedDegrees * Math.PI / 180.0;
            AssertNearly(ReadNumber(value, "RotationRadians", "Rotation"), expectedRadians, "RotationRadians");
        }

        private static void AssertColor(object value, string expectedHex)
        {
            object color = ReadRequired(value, "Color", "ColorHex");
            if (color is string text)
            {
                if (string.Equals(text.TrimStart('#'), expectedHex, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            else if (color is byte or ushort or uint or ulong or sbyte or short or int or long)
            {
                uint packed = Convert.ToUInt32(color, CultureInfo.InvariantCulture);
                uint expected = uint.Parse(expectedHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if ((packed & 0x00FF_FFFFu) == expected || (packed >> 8) == expected)
                    return;
            }
            else
            {
                double r = ReadNumber(color, "R", "Red");
                double g = ReadNumber(color, "G", "Green");
                double b = ReadNumber(color, "B", "Blue");
                int expectedR = int.Parse(expectedHex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int expectedG = int.Parse(expectedHex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int expectedB = int.Parse(expectedHex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                double multiplier = Math.Max(r, Math.Max(g, b)) <= 1.0 ? 255.0 : 1.0;
                if (Math.Abs(r * multiplier - expectedR) <= 0.5 &&
                    Math.Abs(g * multiplier - expectedG) <= 0.5 &&
                    Math.Abs(b * multiplier - expectedB) <= 0.5)
                {
                    return;
                }
            }

            throw new InvalidOperationException("Static decoration color was not preserved.");
        }

        private static void AssertNearly(double actual, double expected, string name)
        {
            if (Math.Abs(actual - expected) > 0.0001)
                throw new InvalidOperationException($"Static decoration {name} is {actual}, expected {expected}.");
        }

        private static int ReadInt(object value, params string[] names) =>
            Convert.ToInt32(ReadRequired(value, names), CultureInfo.InvariantCulture);

        private static double ReadNumber(object value, params string[] names) =>
            Convert.ToDouble(ReadRequired(value, names), CultureInfo.InvariantCulture);

        private static string ReadString(object value, params string[] names) =>
            Convert.ToString(ReadRequired(value, names), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException($"Required member {names[0]} is null.");

        private static object ReadRequired(object value, params string[] names)
        {
            return TryRead(value, names)
                ?? throw new InvalidOperationException(
                    $"Static decoration contract member {string.Join("/", names)} is missing.");
        }

        private static object? TryRead(object value, params string[] names)
        {
            Type type = value.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo? property = type.GetProperties(flags)
                .FirstOrDefault(candidate => names.Contains(candidate.Name, StringComparer.Ordinal));
            if (property is not null)
                return property.GetValue(value);
            FieldInfo? field = type.GetFields(flags)
                .FirstOrDefault(candidate => names.Contains(candidate.Name, StringComparer.Ordinal));
            return field?.GetValue(value);
        }

        private static PropertyInfo? FindProperty(Type type, params string[] names) =>
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(property => names.Contains(property.Name, StringComparer.Ordinal));

        private static IReadOnlyList<object> Enumerate(object? value)
        {
            if (value is not IEnumerable enumerable)
                throw new InvalidOperationException("Static decoration snapshot collection is null.");
            return enumerable.Cast<object>().ToArray();
        }
    }
}
