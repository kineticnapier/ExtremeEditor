namespace ExtremeEditor.Core;

public static class FlatTimingMapBuilder
{
    private const double TwoPi = Math.PI * 2.0;
    private const double PiStock = 3.1415927410125732;
    private const double ThreePlanetAngleOffset = PiStock / 3.0;
    private const double InitialEntryAngle = 4.71238898038469;

    public static TimingMap Build(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);

        int floorCount = level.FloorCount;
        if (floorCount == 0)
            return new TimingMap([]);

        var entryAngles = new double[floorCount];
        var exitAngles = new double[floorCount];
        entryAngles[0] = InitialEntryAngle;

        for (int floor = 0; floor < floorCount - 1; floor++)
        {
            double raw = level.Angles[floor];
            double exit = Math.Abs(raw - 999.0) < 0.000001
                ? entryAngles[floor]
                : (-raw + 90.0) * Math.PI / 180.0;
            exitAngles[floor] = exit;
            entryAngles[floor + 1] = TimingMap.Mod(exit + Math.PI, TwoPi);
        }
        exitAngles[^1] = entryAngles[^1] + PiStock;

        var timings = new FloorTiming[floorCount];
        double bpm = level.InitialBpm > 0 ? level.InitialBpm : 100.0;
        bool isCcw = false;
        bool threePlanets = false;
        double time = 0.0;

        LevelActionStore store = level.ActionStore;
        int actionFloorIndex = 0;
        while (actionFloorIndex < store.ActionFloorCount && store.GetFloor(actionFloorIndex) < 0)
            actionFloorIndex++;

        for (int floor = 0; floor < floorCount; floor++)
        {
            double pauseSeconds = 0.0;
            if (actionFloorIndex < store.ActionFloorCount && store.GetFloor(actionFloorIndex) == floor)
            {
                ReadOnlySpan<LevelAction> actions = store.GetActionsAt(actionFloorIndex++);
                foreach (LevelAction action in actions)
                {
                    if (!action.Active)
                        continue;

                    switch (action.Kind)
                    {
                        case LevelActionKind.Twirl:
                            isCcw = !isCcw;
                            break;
                        case LevelActionKind.MultiPlanet:
                            if (string.Equals(action.Planets, "ThreePlanets", StringComparison.OrdinalIgnoreCase))
                                threePlanets = true;
                            else if (string.Equals(action.Planets, "TwoPlanets", StringComparison.OrdinalIgnoreCase))
                                threePlanets = false;
                            break;
                        case LevelActionKind.SetSpeed:
                            if (string.Equals(action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
                                action.BpmMultiplier is double multiplier && multiplier > 0)
                            {
                                bpm *= multiplier;
                            }
                            else if (action.BeatsPerMinute is double target && target > 0)
                            {
                                bpm = target;
                            }
                            break;
                        case LevelActionKind.Pause when action.Duration is double pauseBeats && pauseBeats > 0:
                            pauseSeconds += pauseBeats * (60.0 / bpm);
                            break;
                    }
                }
            }

            bool midSpin = floor < level.Angles.Length &&
                           Math.Abs(level.Angles[floor] - 999.0) < 0.000001;
            bool previousMidSpin = floor > 0 &&
                                   floor - 1 < level.Angles.Length &&
                                   Math.Abs(level.Angles[floor - 1] - 999.0) < 0.000001;

            double multiplayerOffset = 0.0;
            if (floor > 0 && threePlanets)
            {
                multiplayerOffset = ThreePlanetAngleOffset * (isCcw ? -1.0 : 1.0);
                if (midSpin)
                    multiplayerOffset = 0.0;
                if (previousMidSpin)
                {
                    multiplayerOffset -= (TwoPi + ThreePlanetAngleOffset) *
                                         (isCcw ? -1.0 : 1.0);
                }
            }

            double moved = GetAngleMoved(
                entryAngles[floor] + multiplayerOffset,
                exitAngles[floor] + (midSpin ? multiplayerOffset : 0.0),
                !isCcw);
            if (moved <= 1e-6 || moved >= 6.283184482025146)
                moved = midSpin ? 0.0 : TwoPi;

            double visualEntryAngle = entryAngles[floor];
            if (floor == 0)
            {
                double countdownExtra = Math.Max(0, level.CountdownTicks - 1) * PiStock;
                moved += countdownExtra;
                visualEntryAngle = PiStock * (0.5 - level.CountdownTicks);
            }

            double seconds = moved / PiStock * (60.0 / bpm);
            double exitTime = time + pauseSeconds + seconds;
            timings[floor] = new FloorTiming(
                floor,
                time,
                exitTime,
                visualEntryAngle,
                exitAngles[floor],
                moved,
                bpm,
                isCcw,
                midSpin)
            {
                PauseSeconds = pauseSeconds
            };
            time = exitTime;
        }

        return new TimingMap(timings);
    }

    private static double GetAngleMoved(double entry, double exit, bool isCw)
    {
        double sign = isCw ? 1.0 : -1.0;
        return TimingMap.Mod((exit - entry) * sign, TwoPi);
    }
}
