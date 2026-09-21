namespace ExtremeEditor.Core;

public readonly record struct HitSoundState(string Name, double Volume)
{
    public static HitSoundState FromLevel(LevelDocument level) =>
        new(level.DefaultHitSound, Math.Clamp(level.HitSoundVolumePercent * 0.01, 0.0, 1.0));
}

public readonly record struct HitSoundStateChange(int Floor, HitSoundState State);

public sealed class HitSoundTimeline
{
    private readonly HitSoundState _initialState;
    private readonly HitSoundStateChange[] _changes;

    internal HitSoundTimeline(HitSoundState initialState, HitSoundStateChange[] changes)
    {
        _initialState = initialState;
        _changes = changes;
    }

    public int StateChangeCount => _changes.Length;
    public HitSoundState InitialState => _initialState;
    public IReadOnlyList<HitSoundStateChange> Changes => _changes;

    public HitSoundState GetStateAtFloor(int floor)
    {
        int lo = 0;
        int hi = _changes.Length - 1;
        int best = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_changes[mid].Floor <= floor)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best >= 0 ? _changes[best].State : _initialState;
    }
}

public static class HitSoundTimelineBuilder
{
    public static HitSoundTimeline Build(LevelDocument level)
    {
        HitSoundState initial = HitSoundState.FromLevel(level);
        if (!level.ActionTypeCounts.TryGetValue("SetHitsound", out int setHitSoundCount) ||
            setHitSoundCount <= 0)
        {
            return new HitSoundTimeline(initial, []);
        }

        HitSoundState current = initial;
        var changes = new List<HitSoundStateChange>(Math.Min(setHitSoundCount, 65_536));
        LevelActionStore store = level.ActionStore;

        for (int actionFloorIndex = 0; actionFloorIndex < store.ActionFloorCount; actionFloorIndex++)
        {
            int floor = store.GetFloor(actionFloorIndex);
            ReadOnlySpan<LevelAction> actions = store.GetActionsAt(actionFloorIndex);
            string hitSound = current.Name;
            double volume = current.Volume;
            bool changed = false;

            foreach (LevelAction action in actions)
            {
                if (!action.Active || action.Kind != LevelActionKind.SetHitsound)
                    continue;

                if (!string.Equals(action.GameSound, "Midspin", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(action.HitSound))
                {
                    hitSound = action.HitSound!;
                    changed = true;
                }

                if (action.HitSoundVolumePercent is double volumePercent)
                {
                    volume = Math.Clamp(volumePercent * 0.01, 0.0, 1.0);
                    changed = true;
                }
            }

            if (!changed)
                continue;

            current = new HitSoundState(hitSound, volume);
            if (changes.Count > 0 && changes[^1].Floor == floor)
                changes[^1] = new HitSoundStateChange(floor, current);
            else
                changes.Add(new HitSoundStateChange(floor, current));
        }

        return new HitSoundTimeline(initial, changes.ToArray());
    }
}
