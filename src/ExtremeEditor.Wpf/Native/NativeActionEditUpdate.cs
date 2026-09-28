using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal enum NativeActionEditKind
{
    NoNativeChange,
    FloorIconsFrom,
    FullLevel
}

internal readonly record struct NativeActionEditPlan(
    NativeActionEditKind Kind,
    int StartFloor = -1);

internal static class NativeActionEditUpdate
{
    internal static void Apply(
        NativeLevelViewport viewport,
        LevelDocument level,
        LevelAction before,
        LevelAction after)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        NativeLevelUpdateDiagnostics.RecordActionEditRoutingInvocation();
        NativeActionEditPlan plan = CreatePlan(before, after);
        switch (plan.Kind)
        {
            case NativeActionEditKind.NoNativeChange:
                return;
            case NativeActionEditKind.FloorIconsFrom:
                if (viewport.TryUpdateFloorIconsFrom(level, plan.StartFloor))
                    return;
                viewport.SetLevel(level);
                return;
            default:
                viewport.SetLevel(level);
                return;
        }
    }

    internal static NativeActionEditPlan CreatePlan(LevelAction before, LevelAction after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (IsSetHitsoundAudioPropertyOnlyEdit(before, after))
            return new NativeActionEditPlan(NativeActionEditKind.NoNativeChange);

        if (IsTwirlActiveOnlyEdit(before, after))
            return new NativeActionEditPlan(NativeActionEditKind.FloorIconsFrom, after.Floor);

        return new NativeActionEditPlan(NativeActionEditKind.FullLevel);
    }

    private static bool IsTwirlActiveOnlyEdit(LevelAction before, LevelAction after) =>
        string.Equals(before.EventType, "Twirl", StringComparison.Ordinal) &&
        string.Equals(after.EventType, "Twirl", StringComparison.Ordinal) &&
        before.Kind == LevelActionKind.Twirl &&
        after.Kind == LevelActionKind.Twirl &&
        before.Active != after.Active &&
        before.Floor == after.Floor &&
        before.SourceIndex == after.SourceIndex &&
        string.Equals(before.SpeedType, after.SpeedType, StringComparison.Ordinal) &&
        before.BeatsPerMinute == after.BeatsPerMinute &&
        before.BpmMultiplier == after.BpmMultiplier &&
        string.Equals(before.CustomIcon, after.CustomIcon, StringComparison.Ordinal) &&
        before.SpeedRatio == after.SpeedRatio &&
        string.Equals(before.HitSound, after.HitSound, StringComparison.Ordinal) &&
        before.HitSoundVolumePercent == after.HitSoundVolumePercent &&
        string.Equals(before.GameSound, after.GameSound, StringComparison.Ordinal) &&
        string.Equals(before.Planets, after.Planets, StringComparison.Ordinal) &&
        before.AngleOffset == after.AngleOffset &&
        before.Duration == after.Duration;

    private static bool IsSetHitsoundAudioPropertyOnlyEdit(LevelAction before, LevelAction after)
    {
        if (!string.Equals(before.EventType, "SetHitsound", StringComparison.Ordinal) ||
            !string.Equals(after.EventType, "SetHitsound", StringComparison.Ordinal))
        {
            return false;
        }

        // HitSound, HitSoundVolumePercent, and GameSound are intentionally absent:
        // they affect the audio schedule but are not inputs to the native snapshot.
        // Keep every other modeled value stable so this narrow fast path cannot
        // accidentally absorb another action-edit category.
        return before.Floor == after.Floor &&
               before.Active == after.Active &&
               before.SourceIndex == after.SourceIndex &&
               before.Kind == after.Kind &&
               string.Equals(before.SpeedType, after.SpeedType, StringComparison.Ordinal) &&
               before.BeatsPerMinute == after.BeatsPerMinute &&
               before.BpmMultiplier == after.BpmMultiplier &&
               string.Equals(before.CustomIcon, after.CustomIcon, StringComparison.Ordinal) &&
               before.SpeedRatio == after.SpeedRatio &&
               string.Equals(before.Planets, after.Planets, StringComparison.Ordinal) &&
               before.AngleOffset == after.AngleOffset &&
               before.Duration == after.Duration;
    }
}
