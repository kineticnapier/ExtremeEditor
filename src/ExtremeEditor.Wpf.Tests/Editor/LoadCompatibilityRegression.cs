using System.IO;
using System.Text;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class LoadCompatibilityRegression
{
    internal static void Run()
    {
        VerifyFlatCustomBackground();
        VerifyUtf16DedicatedReaders();
        VerifyLooseBackgroundReader();
    }

    private static void VerifyFlatCustomBackground()
    {
        string path = WriteUtf8("""
            {
              "angleData": [0, 90],
              "settings": { "bpm": 100 },
              "actions": [{
                "floor": 1, "eventType": "CustomBackground", "color": "123456",
                "bgImage": "scene.png", "imageColor": "abcdef", "parallax": [25, 75],
                "bgDisplayMode": "Tiled", "imageSmoothing": false, "lockRot": true,
                "loopBG": true, "scalingRatio": 125
              }]
            }
            """);
        try
        {
            LevelAction action = AdoFaiLoader.LoadFlatAsync(path).GetAwaiter().GetResult()
                .Document.ActionsByFloor[1].Single();
            BackgroundSourceState state = BackgroundSourceReader.ApplyEvent(
                BackgroundSourceReader.ReadRoot(null), action.PropertyOverrides);
            Require(state.Color == "123456" && state.Image == "scene.png", "Flat loader dropped CustomBackground strings.");
            Require(state.ParallaxX == 25 && state.ParallaxY == 75 && state.ScalingRatio == 125,
                "Flat loader dropped CustomBackground numeric properties.");
            Require(!state.ImageSmoothing && state.LockRotation && state.Loop,
                "Flat loader dropped CustomBackground flags.");
        }
        finally { File.Delete(path); }
    }

    private static void VerifyUtf16DedicatedReaders()
    {
        const string json = """
            {
              "angleData": [0, 90],
              "settings": {
                "bpm": 100, "position": [3, 4], "rotation": 12, "zoom": 150,
                "trackColorType": "Glow", "trackColor": "123456", "secondaryTrackColor": "abcdef"
              },
              "actions": [
                { "floor": 1, "eventType": "MoveCamera", "position": [8, 9], "duration": 1 },
                { "floor": 1, "eventType": "PositionTrack", "positionOffset": [2, 3] },
                { "floor": 1, "eventType": "ColorTrack", "trackColor": "fedcba" }
              ]
            }
            """;
        string utf8 = WriteUtf8(json);
        string utf16Le = Write(json, new UnicodeEncoding(false, true));
        string utf16Be = Write(json, new UnicodeEncoding(true, true));
        try
        {
            LevelCameraSettings expected = WpfLevelLoader.Load(utf8).Document.CameraSettings;
            LevelDocument expectedDocument = WpfLevelLoader.Load(utf8).Document;
            TrackVisualSourceData expectedVisual = TrackVisualMetadataCache.Get(expectedDocument);
            TrackTransformSourceData expectedTransforms = TrackTransformMetadataCache.Get(expectedDocument);
            CameraSourceData expectedCamera = CameraMetadataCache.Get(expectedDocument);
            foreach (string path in new[] { utf16Le, utf16Be })
            {
                LevelDocument actual = WpfLevelLoader.Load(path).Document;
                Require(actual.Angles.SequenceEqual(new[] { 0d, 90d }), "UTF-16 core path differs from UTF-8.");
                Require(actual.CameraSettings == expected, "UTF-16 dedicated camera reader differs from UTF-8.");
                TrackVisualSourceData visual = TrackVisualMetadataCache.Get(actual);
                TrackTransformSourceData transforms = TrackTransformMetadataCache.Get(actual);
                CameraSourceData camera = CameraMetadataCache.Get(actual);
                Require(visual.InitialStyle == expectedVisual.InitialStyle && visual.Events.Length == expectedVisual.Events.Length,
                    "UTF-16 track visual metadata differs from UTF-8.");
                Require(transforms.Events.Length == expectedTransforms.Events.Length &&
                        transforms.Events[0] == expectedTransforms.Events[0],
                    "UTF-16 track transform metadata differs from UTF-8.");
                Require(camera.Events.Length == expectedCamera.Events.Length && camera.Events[0] == expectedCamera.Events[0],
                    "UTF-16 camera event metadata differs from UTF-8.");
            }
        }
        finally { File.Delete(utf8); File.Delete(utf16Le); File.Delete(utf16Be); }
    }

    private static void VerifyLooseBackgroundReader()
    {
        string path = WriteUtf8("{\n\"angleData\":[0],\n\"settings\":{\"bpm\":100,\"bgImage\":\"line1\nline2.png\",},\n\"actions\":[]\n}");
        try
        {
            LevelDocument level = WpfLevelLoader.Load(path).Document;
            BackgroundSourceState state = BackgroundSourceReader.ReadInitial(level);
            Require(state.Image == "line1\nline2.png", "Loose JSON background metadata could not be reparsed.");
        }
        finally { File.Delete(path); }
    }

    private static string WriteUtf8(string json) => Write(json, new UTF8Encoding(false));

    private static string Write(string json, Encoding encoding)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ExtremeEditor-load-compat-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, json, encoding);
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
