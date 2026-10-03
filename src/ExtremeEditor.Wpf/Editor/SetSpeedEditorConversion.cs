using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf;

internal static class SetSpeedEditorConversion
{
    internal static bool TryGetPreviousEffectiveBpm(
        LevelDocument level,
        LevelAction target,
        out double bpm)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(target);

        bpm = IsValidSpeedValue(level.InitialBpm) ? level.InitialBpm : 100.0;
        LevelActionStore store = level.ActionStore;

        for (int actionFloorIndex = 0; actionFloorIndex < store.ActionFloorCount; actionFloorIndex++)
        {
            ReadOnlySpan<LevelAction> actions = store.GetActionsAt(actionFloorIndex);
            foreach (LevelAction action in actions)
            {
                if (IsSameAction(action, target))
                    return true;

                if (action.Floor > target.Floor)
                    return true;

                ApplySetSpeed(action, ref bpm);
            }
        }

        return target.Floor >= 0;
    }

    internal static bool TryConvertBpmToMultiplier(
        double previousEffectiveBpm,
        double beatsPerMinute,
        out double multiplier)
    {
        multiplier = 0.0;
        if (!IsValidSpeedValue(previousEffectiveBpm) || !IsValidSpeedValue(beatsPerMinute))
            return false;

        multiplier = beatsPerMinute / previousEffectiveBpm;
        return IsValidSpeedValue(multiplier);
    }

    internal static bool TryConvertMultiplierToBpm(
        double previousEffectiveBpm,
        double multiplier,
        out double beatsPerMinute)
    {
        beatsPerMinute = 0.0;
        if (!IsValidSpeedValue(previousEffectiveBpm) || !IsValidSpeedValue(multiplier))
            return false;

        beatsPerMinute = previousEffectiveBpm * multiplier;
        return IsValidSpeedValue(beatsPerMinute);
    }

    internal static bool SynchronizeDraft(
        LevelDocument level,
        LevelAction action,
        JsonObject current,
        JsonObject draft)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);

        if (action.Kind != LevelActionKind.SetSpeed ||
            !TryGetPreviousEffectiveBpm(level, action, out double previousBpm))
        {
            return false;
        }

        string oldType = ReadString(current["speedType"]) ?? action.SpeedType ?? "Bpm";
        string newType = ReadString(draft["speedType"]) ?? oldType;
        bool typeChanged = !string.Equals(oldType, newType, StringComparison.OrdinalIgnoreCase);
        bool bpmChanged = !JsonNode.DeepEquals(current["beatsPerMinute"], draft["beatsPerMinute"]);
        bool multiplierChanged = !JsonNode.DeepEquals(current["bpmMultiplier"], draft["bpmMultiplier"]);

        if (typeChanged)
        {
            // A mode switch changes only the representation. Preserve the effective
            // speed selected by the old mode and derive the newly-primary field.
            if (string.Equals(oldType, "Multiplier", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadPositiveDouble(current["bpmMultiplier"], out double multiplier) ||
                    !TryConvertMultiplierToBpm(previousBpm, multiplier, out double targetBpm))
                {
                    return false;
                }

                draft["beatsPerMinute"] = JsonValue.Create(targetBpm);
                draft["bpmMultiplier"] = JsonValue.Create(multiplier);
                return true;
            }

            if (!TryReadPositiveDouble(current["beatsPerMinute"], out double bpm) ||
                !TryConvertBpmToMultiplier(previousBpm, bpm, out double convertedMultiplier))
            {
                return false;
            }

            draft["beatsPerMinute"] = JsonValue.Create(bpm);
            draft["bpmMultiplier"] = JsonValue.Create(convertedMultiplier);
            return true;
        }

        // Property-grid editing changes one field at a time. Whichever numeric
        // representation the user edited becomes the source for the reciprocal
        // conversion, independent of the current display mode.
        if (bpmChanged && !multiplierChanged)
        {
            if (!TryReadPositiveDouble(draft["beatsPerMinute"], out double bpm) ||
                !TryConvertBpmToMultiplier(previousBpm, bpm, out double multiplier))
            {
                Restore(current, draft, "beatsPerMinute");
                return true;
            }

            draft["bpmMultiplier"] = JsonValue.Create(multiplier);
            return true;
        }

        if (multiplierChanged && !bpmChanged)
        {
            if (!TryReadPositiveDouble(draft["bpmMultiplier"], out double multiplier) ||
                !TryConvertMultiplierToBpm(previousBpm, multiplier, out double bpm))
            {
                Restore(current, draft, "bpmMultiplier");
                return true;
            }

            draft["beatsPerMinute"] = JsonValue.Create(bpm);
            return true;
        }

        return false;
    }

    private static void ApplySetSpeed(LevelAction action, ref double bpm)
    {
        if (action.Kind != LevelActionKind.SetSpeed || !action.Active)
            return;

        if (string.Equals(action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
            action.BpmMultiplier is double multiplier &&
            IsValidSpeedValue(multiplier))
        {
            bpm *= multiplier;
            return;
        }

        if (action.BeatsPerMinute is double target && IsValidSpeedValue(target))
            bpm = target;
    }

    private static bool IsSameAction(LevelAction left, LevelAction right)
    {
        if (ReferenceEquals(left, right))
            return true;

        return left.SourceIndex != -1 &&
               right.SourceIndex != -1 &&
               left.SourceIndex == right.SourceIndex &&
               left.Floor == right.Floor;
    }

    private static bool TryReadPositiveDouble(JsonNode? node, out double value)
    {
        value = 0.0;
        if (node is not JsonValue json)
            return false;

        if (!json.TryGetValue(out value))
        {
            if (!json.TryGetValue(out int integer))
                return false;
            value = integer;
        }

        return IsValidSpeedValue(value);
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue json && json.TryGetValue(out string? value)
            ? value
            : null;
    }

    private static void Restore(JsonObject current, JsonObject draft, string propertyName)
    {
        draft[propertyName] = current[propertyName]?.DeepClone();
    }

    private static bool IsValidSpeedValue(double value) =>
        double.IsFinite(value) && value > 0.0;
}
