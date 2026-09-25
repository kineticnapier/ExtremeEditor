using System.Reflection;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class CameraRuntimeTileReferenceRegression
{
    public static void Run()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type cameraEventType = assembly.GetType("ExtremeEditor.Wpf.Native.NativeCameraEvent")
            ?? throw new InvalidOperationException("NativeCameraEvent is missing.");

        FieldInfo referenceFloor = cameraEventType.GetField(
            "ReferenceFloor",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "RED: NativeCameraEvent must carry the reference floor so MoveCamera relativeTo=Tile can resolve the current runtime tile position instead of baking LevelDocument.Positions during timeline construction.");

        if (referenceFloor.FieldType != typeof(int))
            throw new InvalidOperationException("NativeCameraEvent.ReferenceFloor must be an Int32 floor index.");

        FieldInfo referenceFlags = cameraEventType.GetField(
            "ReferenceFlags",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "RED: NativeCameraEvent must identify its runtime camera reference frame.");

        if (referenceFlags.FieldType != typeof(uint))
            throw new InvalidOperationException("NativeCameraEvent.ReferenceFlags must be UInt32 ABI flags.");

        FieldInfo tileFlag = cameraEventType.GetField(
            "FlagReferenceTile",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "RED: NativeCameraEvent must expose a Tile runtime-reference flag.");

        if (!tileFlag.IsLiteral || tileFlag.FieldType != typeof(uint))
            throw new InvalidOperationException("NativeCameraEvent.FlagReferenceTile must be a UInt32 constant.");

        uint value = (uint)(tileFlag.GetRawConstantValue() ?? 0u);
        if (value == 0u)
            throw new InvalidOperationException("NativeCameraEvent.FlagReferenceTile must be non-zero.");
    }
}
