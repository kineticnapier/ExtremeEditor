using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class TrackColorActionRecovery
{
    internal static int MergeMissing(LevelDocument level, TrackColorSourceData source)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(source);

        if (source.Events.Length == 0)
            return 0;

        var actions = level.ActionStore.Actions.ToList();
        var remainingExisting = actions
            .Where(static action => action.EventType is "ColorTrack" or "RecolorTrack")
            .GroupBy(static action => (action.Floor, action.EventType))
            .ToDictionary(static group => group.Key, static group => group.Count());

        int recovered = 0;
        foreach (TrackColorSourceEvent item in source.Events.OrderBy(static item => item.SourceIndex))
        {
            var key = (item.Floor, item.EventType);
            if (remainingExisting.TryGetValue(key, out int existing) && existing > 0)
            {
                remainingExisting[key] = existing - 1;
                continue;
            }

            actions.Add(new LevelAction(
                item.Floor,
                item.EventType,
                item.Active,
                null,
                null,
                null,
                null)
            {
                SourceIndex = item.SourceIndex
            });
            recovered++;
        }

        if (recovered > 0)
            level.ReplaceActions(actions);

        return recovered;
    }
}
