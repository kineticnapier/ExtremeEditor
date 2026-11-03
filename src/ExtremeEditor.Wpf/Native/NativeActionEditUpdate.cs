using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal enum NativeActionEditKind
{
    NoNativeChange,
    FloorIconsFrom,
    FlashTimeline,
    FullLevel
}

internal readonly record struct NativeActionEditPlan(
    NativeActionEditKind Kind,
    int StartFloor = -1);

internal static class NativeActionEditUpdate
{
    internal static NativeActionEditKind Apply(
        NativeLevelViewport viewport,
        LevelDocument level,
        LevelAction? before,
        LevelAction? after,
        TimingMap? timingMap = null)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(level);

        NativeLevelUpdateDiagnostics.RecordActionEditRoutingInvocation();
        NativeActionEditPlan plan = CreatePlan(before, after);
        switch (plan.Kind)
        {
            case NativeActionEditKind.NoNativeChange:
                return plan.Kind;
            case NativeActionEditKind.FloorIconsFrom:
                if (viewport.TryUpdateFloorIconsFrom(level, plan.StartFloor))
                    return plan.Kind;
                viewport.SetLevel(level);
                return NativeActionEditKind.FullLevel;
            case NativeActionEditKind.FlashTimeline:
                // The Flash route keeps the caller's timing map, hit sounds and
                // every other timeline. Without a current timing map, or when the
                // floor icon cannot be resolved against the uploaded assets, keep
                // the full replacement; the caller then rebuilds playback too.
                if (timingMap is not null &&
                    viewport.TryUpdateFlashTimeline(level, timingMap, plan.StartFloor))
                    return plan.Kind;
                viewport.SetLevel(level);
                return NativeActionEditKind.FullLevel;
            default:
                viewport.SetLevel(level);
                return NativeActionEditKind.FullLevel;
        }
    }

    internal static NativeActionEditPlan CreatePlan(LevelAction? before, LevelAction? after)
    {
        if (IsFlashOnlyEdit(before, after))
            return new NativeActionEditPlan(NativeActionEditKind.FlashTimeline, (after ?? before)!.Floor);

        if (before is null || after is null)
            return new NativeActionEditPlan(NativeActionEditKind.FullLevel);

        if (IsSetHitsoundAudioPropertyOnlyEdit(before, after))
            return new NativeActionEditPlan(NativeActionEditKind.NoNativeChange);

        if (IsTwirlActiveOnlyEdit(before, after))
            return new NativeActionEditPlan(NativeActionEditKind.FloorIconsFrom, after.Floor);

        return new NativeActionEditPlan(NativeActionEditKind.FullLevel);
    }

    // Flash events feed only the Flash timeline and the floor icon. They do not
    // change angleData, timing, hit sounds or any other native timeline. A
    // floor move touches two icons and stays on the full path.
    private static bool IsFlashOnlyEdit(LevelAction? before, LevelAction? after)
    {
        if (before is null && after is null)
            return false;
        if (before is not null && !IsFlash(before))
            return false;
        if (after is not null && !IsFlash(after))
            return false;
        return before is null || after is null ||
               (before.Floor == after.Floor && before.SourceIndex == after.SourceIndex);
    }

    private static bool IsFlash(LevelAction action) =>
        string.Equals(action.EventType, "Flash", StringComparison.Ordinal) &&
        action.Kind == LevelActionKind.Unknown;

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
