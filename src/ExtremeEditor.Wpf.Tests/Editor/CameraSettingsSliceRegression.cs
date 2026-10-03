using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ExtremeEditor.Core;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class CameraSettingsSliceRegression
{
    private const string CameraTypeName = "ExtremeEditor.Core.LevelCameraSettings";
    private const string SnapshotTypeName = "ExtremeEditor.Wpf.CameraSettingsSnapshot";

    [ModuleInitializer]
    internal static void InitializeIsolatedRun()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("EXTREMEEDITOR_CAMERA_SETTINGS_ONLY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Run();
            Console.WriteLine("PASS: Camera Settings slice regression is valid.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {Unwrap(ex).Message}");
            Environment.Exit(1);
        }
    }

    internal static void Run()
    {
        var failures = new List<string>();
        Check(failures, "typed camera model/load", VerifyTypedCameraModelAndLoad);
        Check(failures, "typed camera edit/undo", VerifyTypedCameraEditContract);
        Check(failures, "camera save ownership", VerifyCameraSaveContract);
        Check(failures, "camera runtime refresh", VerifyCameraRuntimeRefreshContract);
        Check(failures, "settings sidebar camera category", VerifySidebarContract);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "Camera Settings Slice 2 is not available:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(static failure => $"- {failure}")));
        }
    }

    private static void VerifyTypedCameraModelAndLoad()
    {
        Assembly core = typeof(LevelDocument).Assembly;
        Type cameraType = core.GetType(CameraTypeName)
            ?? throw new InvalidOperationException("typed LevelCameraSettings is missing.");
        PropertyInfo cameraProperty = typeof(LevelDocument).GetProperty("CameraSettings")
            ?? throw new InvalidOperationException("LevelDocument.CameraSettings is missing.");
        if (cameraProperty.PropertyType != cameraType)
            throw new InvalidOperationException("LevelDocument.CameraSettings does not use LevelCameraSettings.");

        foreach (string propertyName in new[] { "RelativeTo", "PositionX", "PositionY", "Rotation", "Zoom" })
        {
            if (cameraType.GetProperty(propertyName) is null)
                throw new InvalidOperationException($"LevelCameraSettings.{propertyName} is missing.");
        }

        string path = WriteFixture("Load");
        try
        {
            AssertCamera(AdoFaiLoader.Load(path).Document, cameraProperty, "normal loader");
            AssertCamera(AdoFaiLoader.LoadAsync(path).GetAwaiter().GetResult().Document, cameraProperty, "streaming loader");
            AssertCamera(AdoFaiLoader.LoadFlatAsync(path).GetAwaiter().GetResult().Document, cameraProperty, "flat streaming loader");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void VerifyTypedCameraEditContract()
    {
        Type snapshotType = typeof(EditorSession).Assembly.GetType(SnapshotTypeName)
            ?? throw new InvalidOperationException("typed CameraSettingsSnapshot is missing.");
        MethodInfo get = typeof(EditorSession).GetMethod(
            "GetCameraSettings",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EditorSession.GetCameraSettings is missing.");
        MethodInfo edit = typeof(EditorSession).GetMethod(
            "EditCameraSettings",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EditorSession.EditCameraSettings is missing.");
        if (get.ReturnType != snapshotType)
            throw new InvalidOperationException("GetCameraSettings does not return CameraSettingsSnapshot.");
        if (edit.GetParameters().Length != 1 || edit.GetParameters()[0].ParameterType != snapshotType)
            throw new InvalidOperationException("EditCameraSettings does not accept CameraSettingsSnapshot.");

        foreach (string propertyName in new[] { "RelativeTo", "PositionX", "PositionY", "Rotation", "Zoom" })
        {
            if (snapshotType.GetProperty(propertyName) is null)
                throw new InvalidOperationException($"CameraSettingsSnapshot.{propertyName} is missing.");
        }
    }

    private static void VerifyCameraSaveContract()
    {
        Type saveService = typeof(EditorSession).Assembly.GetType("ExtremeEditor.Wpf.AdoFaiEditorSaveService")
            ?? throw new InvalidOperationException("AdoFaiEditorSaveService is missing.");
        MethodInfo? writer = saveService.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name.Contains("Settings", StringComparison.OrdinalIgnoreCase));
        if (writer is null)
        {
            throw new InvalidOperationException(
                "save service exposes no settings-owned write path for camera values/unknown-property preservation.");
        }
    }

    private static void VerifyCameraRuntimeRefreshContract()
    {
        Type mainWindow = typeof(EditorSession).Assembly.GetType("ExtremeEditor.Wpf.MainWindow")
            ?? throw new InvalidOperationException("MainWindow is missing.");
        MethodInfo? refresh = mainWindow.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                method.Name.Contains("CameraSettings", StringComparison.Ordinal) &&
                method.Name.Contains("Refresh", StringComparison.Ordinal));
        if (refresh is null)
        {
            throw new InvalidOperationException(
                "camera settings have no dedicated runtime refresh path for rebuilding the initial camera timeline.");
        }
    }

    private static void VerifySidebarContract()
    {
        string path = Path.Combine(
            Environment.CurrentDirectory,
            "src",
            "ExtremeEditor.Wpf",
            "MainWindow.xaml");
        XDocument xaml = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        XElement root = xaml.Root ?? throw new InvalidOperationException("MainWindow.xaml has no root element.");

        XElement sidebar = RequireNamedElement(root, "SettingsSidebarHost");
        _ = RequireNamedElement(root, "SongSettingsPane");
        XElement cameraPane = RequireNamedElement(root, "CameraSettingsPane");
        _ = RequireNamedElement(root, "SongSettingsCategoryButton");
        _ = RequireNamedElement(root, "CameraSettingsCategoryButton");
        _ = RequireNamedElement(root, "SettingsSidebarCollapseButton");
        XElement inspector = RequireNamedElement(root, "EventInspectorHost");

        foreach (string control in new[]
                 {
                     "CameraRelativeToComboBox",
                     "CameraPositionXTextBox",
                     "CameraPositionYTextBox",
                     "CameraRotationTextBox",
                     "CameraZoomTextBox"
                 })
        {
            _ = RequireNamedElement(root, control);
        }

        if (cameraPane.AncestorsAndSelf().Contains(inspector) || inspector.AncestorsAndSelf().Contains(sidebar))
            throw new InvalidOperationException("Camera Settings must remain independent from Event Inspector.");
    }

    private static void AssertCamera(LevelDocument document, PropertyInfo property, string label)
    {
        object camera = property.GetValue(document)
            ?? throw new InvalidOperationException($"{label}: CameraSettings is null.");
        Type type = camera.GetType();
        AssertString("Player", type.GetProperty("RelativeTo")?.GetValue(camera), $"{label} relativeTo");
        AssertNear(1.25, type.GetProperty("PositionX")?.GetValue(camera), $"{label} position X");
        AssertNear(-2.5, type.GetProperty("PositionY")?.GetValue(camera), $"{label} position Y");
        AssertNear(30.0, type.GetProperty("Rotation")?.GetValue(camera), $"{label} rotation");
        AssertNear(125.0, type.GetProperty("Zoom")?.GetValue(camera), $"{label} zoom");
    }

    private static string WriteFixture(string name)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExtremeEditor-CameraSettings-{name}-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(path, """
        {
          "angleData": [0, 90, 180],
          "settings": {
            "bpm": 100,
            "relativeTo": "Player",
            "position": [1.25, -2.5],
            "rotation": 30,
            "zoom": 125,
            "futureCameraSetting": { "token": "keep-me" }
          },
          "actions": [
            {
              "floor": 1,
              "eventType": "MoveCamera",
              "active": true,
              "duration": 1,
              "relativeTo": "Tile",
              "position": [3, 4],
              "rotation": 45,
              "zoom": 90
            }
          ],
          "decorations": []
        }
        """);
        return path;
    }

    private static XElement RequireNamedElement(XElement root, string name) =>
        root.DescendantsAndSelf().FirstOrDefault(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" &&
                string.Equals(attribute.Value, name, StringComparison.Ordinal)))
        ?? throw new InvalidOperationException($"Camera Settings XAML element '{name}' is missing.");

    private static void AssertString(string expected, object? actual, string label)
    {
        if (!string.Equals(expected, Convert.ToString(actual), StringComparison.Ordinal))
            throw new InvalidOperationException($"{label}: expected={expected}, actual={actual ?? "<null>"}.");
    }

    private static void AssertNear(double expected, object? actual, string label)
    {
        double value = actual is null ? double.NaN : Convert.ToDouble(actual);
        if (!double.IsFinite(value) || Math.Abs(expected - value) > 1.0e-8)
            throw new InvalidOperationException($"{label}: expected={expected}, actual={value}.");
    }

    private static void Check(List<string> failures, string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {Unwrap(ex).Message}");
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException invocation && invocation.InnerException is Exception inner)
            ex = inner;
        return ex;
    }
}
