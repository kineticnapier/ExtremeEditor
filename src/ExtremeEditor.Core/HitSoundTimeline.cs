namespace ExtremeEditor.Core;

public readonly record struct HitSoundState(string Name, double Volume)
{
    public static HitSoundState FromLevel(LevelDocument level) =>
        new(level.DefaultHitSound, Math.Clamp(level.HitSoundVolumePercent * 0.01, 0.0, 1.0));
}

public readonly record struct HitSoundStateChange(int Floor, HitSoundState State);

/// <summary>
/// Stores only floors where the normal hitsound state changes. This deliberately
/// avoids allocating one cue object per floor on multi-million-floor levels.
/// </summary>
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
        HitSoundState current = HitSoundState.FromLevel(level);
        var changes = new List<HitSoundStateChange>();

        foreach ((int floor, LevelAction[] actions) in level.ActionsByFloor.OrderBy(pair => pair.Key))
        {
            string hitSound = current.Name;
            double volume = current.Volume;
            bool changed = false;

            foreach (LevelAction action in actions)
            {
                if (!action.Active || !string.Equals(action.EventType, "SetHitsound", StringComparison.Ordinal))
                    continue;

                // Midspin has a separate stock hitsound state. Normal landing
                // sounds should only be changed by Hitsound/default SetHitsound.
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

        return new HitSoundTimeline(HitSoundState.FromLevel(level), changes.ToArray());
    }
}
