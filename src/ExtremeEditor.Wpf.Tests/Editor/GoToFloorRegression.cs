using System.Reflection;
using ExtremeEditor.Wpf.Native;

namespace ExtremeEditor.Wpf.Tests;

internal static class GoToFloorRegression
{
    public static void Run()
    {
        VerifyDialogCanBeConstructed();

        const int floorCount = 400_000;
        AssertResolved("1", floorCount, 0);
        AssertResolved("339933", floorCount, 339_932);
        AssertResolved("0", floorCount, 0);
        AssertResolved("-42", floorCount, 0);
        AssertResolved("400001", floorCount, floorCount - 1);
        AssertResolved("2147483648", floorCount, floorCount - 1);
        AssertResolved(long.MaxValue.ToString(), floorCount, floorCount - 1);

        AssertInvalid("");
        AssertInvalid("not a floor");
        AssertInvalid("9223372036854775808");
        AssertInvalid("-9223372036854775809");

        var selection = new EditorSelectionState(floorCount);
        if (GoToFloorNavigation.GetInitialValue(selection) != "1")
            throw new InvalidOperationException("Go to Floor must default to floor 1 when selection is empty.");

        selection.SetSelection([17], 17);
        if (GoToFloorNavigation.GetInitialValue(selection) != "18")
            throw new InvalidOperationException("Go to Floor must initialize from the primary selection.");

        int centeredFloor = -1;
        GoToFloorNavigation.Apply(selection, 339_932, floor => centeredFloor = floor);
        if (!selection.SelectedFloors.SequenceEqual([339_932]) ||
            selection.PrimaryFloor != 339_932 ||
            selection.AnchorFloor != 339_932 ||
            centeredFloor != 339_932)
        {
            throw new InvalidOperationException(
                "Go to Floor must set one selected floor, update primary/anchor, and center that floor.");
        }

        MethodInfo centerFloor = typeof(NativeLevelViewport).GetMethod(
            nameof(NativeLevelViewport.CenterFloor),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("NativeLevelViewport.CenterFloor is missing.");
        if (!centerFloor.GetParameters().Select(static parameter => parameter.ParameterType).SequenceEqual([typeof(int)]))
            throw new InvalidOperationException("NativeLevelViewport.CenterFloor must accept one floor index.");

        Type bridge = typeof(NativeLevelViewport).Assembly.GetType("ExtremeEditor.Wpf.Native.NativeRendererNative")
            ?? throw new InvalidOperationException("NativeRendererNative is missing.");
        if (bridge.GetMethod("CenterAt", BindingFlags.Static | BindingFlags.NonPublic) is null)
            throw new InvalidOperationException("Native renderer camera centering API is missing.");
    }

    private static void VerifyDialogCanBeConstructed()
    {
        var dialog = new GoToFloorDialog(owner: null, initialValue: "339933");
        dialog.Close();
    }

    private static void AssertResolved(string input, int floorCount, int expected)
    {
        if (!GoToFloorNavigation.TryResolveFloor(input, floorCount, out int actual) || actual != expected)
        {
            throw new InvalidOperationException(
                $"Go to Floor conversion failed for '{input}': expected {expected}, actual {actual}.");
        }
    }

    private static void AssertInvalid(string input)
    {
        if (GoToFloorNavigation.TryResolveFloor(input, 400_000, out _))
            throw new InvalidOperationException($"Go to Floor must reject invalid input '{input}'.");
    }
}
