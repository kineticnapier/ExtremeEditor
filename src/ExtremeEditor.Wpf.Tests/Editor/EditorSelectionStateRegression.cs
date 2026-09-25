using System.Collections;
using System.Reflection;
using System.Windows.Input;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class EditorSelectionStateRegression
{
    public static void Run()
    {
        Assembly wpfAssembly = typeof(MainWindow).Assembly;
        Type stateType = wpfAssembly.GetType("ExtremeEditor.Wpf.EditorSelectionState")
            ?? throw new InvalidOperationException("EditorSelectionState does not exist yet.");

        ConstructorInfo constructor = stateType.GetConstructor([typeof(int)])
            ?? throw new InvalidOperationException("EditorSelectionState(int floorCount) constructor is missing.");

        PropertyInfo selectedFloorsProperty = RequireProperty(stateType, "SelectedFloors");
        PropertyInfo primaryFloorProperty = RequireProperty(stateType, "PrimaryFloor");
        PropertyInfo anchorFloorProperty = RequireProperty(stateType, "AnchorFloor");
        EventInfo changedEvent = stateType.GetEvent("Changed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EditorSelectionState.Changed event is missing.");

        MethodInfo selectFloor = RequireMethod(
            stateType,
            "SelectFloor",
            typeof(int),
            typeof(ModifierKeys));
        MethodInfo moveSelection = RequireMethod(
            stateType,
            "MoveSelection",
            typeof(int),
            typeof(bool));
        MethodInfo setSelection = RequireMethod(
            stateType,
            "SetSelection",
            typeof(IEnumerable<int>),
            typeof(int));
        MethodInfo setFloorCount = RequireMethod(
            stateType,
            "SetFloorCount",
            typeof(int));

        object state = constructor.Invoke([10]);
        AssertSelection(state, selectedFloorsProperty, primaryFloorProperty, anchorFloorProperty, [], -1, -1);

        int changedCount = 0;
        EventHandler changedHandler = (_, _) => changedCount++;
        changedEvent.AddEventHandler(state, changedHandler);

        selectFloor.Invoke(state, [3, ModifierKeys.None]);
        AssertSelection(state, selectedFloorsProperty, primaryFloorProperty, anchorFloorProperty, [3], 3, 3);

        selectFloor.Invoke(state, [5, ModifierKeys.Shift]);
        AssertSelection(state, selectedFloorsProperty, primaryFloorProperty, anchorFloorProperty, [3, 4, 5], 5, 3);

        selectFloor.Invoke(state, [4, ModifierKeys.Control]);
        AssertSelection(state, selectedFloorsProperty, primaryFloorProperty, anchorFloorProperty, [3, 5], 5, 5);

        selectFloor.Invoke(state, [1, ModifierKeys.Control]);
        AssertSelection(state, selectedFloorsProperty, primaryFloorProperty, anchorFloorProperty, [1, 3, 5], 1, 1);

        moveSelection.Invoke(state, [4, true]);
        AssertSelection(state, selectedFloorsProperty, primaryFloorProperty, anchorFloorProperty, [1, 2, 3, 4], 4, 1);

        setSelection.Invoke(state, [new[] { 2, 7, 9 }, 9]);
        AssertSelection(state, selectedFloorsProperty, primaryFloorProperty, anchorFloorProperty, [2, 7, 9], 9, 9);

        setFloorCount.Invoke(state, [5]);
        AssertSelection(state, selectedFloorsProperty, primaryFloorProperty, anchorFloorProperty, [2], 2, 2);

        setFloorCount.Invoke(state, [0]);
        AssertSelection(state, selectedFloorsProperty, primaryFloorProperty, anchorFloorProperty, [], -1, -1);

        if (changedCount == 0)
            throw new InvalidOperationException("EditorSelectionState.Changed must fire when selection changes.");

        FieldInfo[] mainWindowFields = typeof(MainWindow).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (!mainWindowFields.Any(field => stateType.IsAssignableFrom(field.FieldType)))
        {
            throw new InvalidOperationException(
                "MainWindow must own the shared EditorSelectionState instance.");
        }

        Type viewportType = typeof(LevelViewport);
        foreach (string legacyFieldName in new[] { "_selectedFloors", "_selectedFloor", "_selectionAnchor" })
        {
            if (viewportType.GetField(legacyFieldName, BindingFlags.Instance | BindingFlags.NonPublic) is not null)
            {
                throw new InvalidOperationException(
                    $"LevelViewport still owns legacy selection field '{legacyFieldName}'; selection state must live outside the viewport.");
            }
        }
    }

    private static PropertyInfo RequireProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"EditorSelectionState.{name} property is missing.");

    private static MethodInfo RequireMethod(Type type, string name, params Type[] parameterTypes)
    {
        MethodInfo? method = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                    return false;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != parameterTypes.Length)
                    return false;

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType != parameterTypes[i])
                        return false;
                }

                return true;
            });

        return method
            ?? throw new InvalidOperationException(
                $"EditorSelectionState.{name}({string.Join(", ", parameterTypes.Select(type => type.Name))}) is missing.");
    }

    private static void AssertSelection(
        object state,
        PropertyInfo selectedFloorsProperty,
        PropertyInfo primaryFloorProperty,
        PropertyInfo anchorFloorProperty,
        int[] expectedFloors,
        int expectedPrimary,
        int expectedAnchor)
    {
        object? selectedValue = selectedFloorsProperty.GetValue(state);
        if (selectedValue is not IEnumerable enumerable)
            throw new InvalidOperationException("EditorSelectionState.SelectedFloors must be enumerable.");

        int[] actualFloors = enumerable.Cast<object>().Select(Convert.ToInt32).OrderBy(value => value).ToArray();
        int[] expectedOrdered = expectedFloors.OrderBy(value => value).ToArray();
        if (!actualFloors.SequenceEqual(expectedOrdered))
        {
            throw new InvalidOperationException(
                $"Selection mismatch: expected [{string.Join(",", expectedOrdered)}], actual [{string.Join(",", actualFloors)}].");
        }

        int actualPrimary = Convert.ToInt32(primaryFloorProperty.GetValue(state));
        if (actualPrimary != expectedPrimary)
            throw new InvalidOperationException($"Primary floor mismatch: expected {expectedPrimary}, actual {actualPrimary}.");

        int actualAnchor = Convert.ToInt32(anchorFloorProperty.GetValue(state));
        if (actualAnchor != expectedAnchor)
            throw new InvalidOperationException($"Anchor floor mismatch: expected {expectedAnchor}, actual {actualAnchor}.");
    }
}
