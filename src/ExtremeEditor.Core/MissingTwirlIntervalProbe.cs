namespace ExtremeEditor.Core;

public sealed record MissingTwirlIntervalProbeResult(
    int BestStartFloor,
    int BestEndFloorExclusive,
    double CurrentDurationSeconds,
    double BestDurationSeconds,
    double DurationChangeSeconds,
    double MaximumReductionSeconds);

/// <summary>
/// Diagnostic probe for two missing Twirls. If two Twirls are missing at the
/// boundaries of an interval, only the floors inside that interval have their
/// CW/CCW parity flipped. The probe finds the interval with the largest timing
/// reduction in O(N) time by minimizing the subarray sum of per-floor
/// flipped-minus-normal duration deltas.
/// </summary>
public static class MissingTwirlIntervalProbe
{
    private const double PiStock = 3.1415927410125732;
    private const double TwoPi = 6.2831854820251465;

    public static MissingTwirlIntervalProbeResult Analyze(
        LevelDocument level,
        TimingMap timing)
    {
        int floorCount = Math.Min(level.FloorCount, timing.Floors.Count);
        if (floorCount <= 1)
        {
            return new MissingTwirlIntervalProbeResult(
                -1,
                -1,
                timing.Duration,
                timing.Duration,
                0.0,
                0.0);
        }

        int[] planets = BuildPlanetCounts(level, timing, floorCount);

        double bestDelta = 0.0;
        int bestStart = -1;
        int bestEndExclusive = -1;

        double currentDelta = 0.0;
        int currentStart = 1;

        for (int floor = 1; floor < floorCount; floor++)
        {
            if (currentDelta > 0.0)
            {
                currentDelta = 0.0;
                currentStart = floor;
            }

            FloorTiming current = timing.Floors[floor];
            double normalSeconds = current.ExitTime - current.EntryTime;
            double flippedSeconds = GetFlippedFloorSeconds(
                timing.Floors,
                floor,
                planets[floor]);

            currentDelta += flippedSeconds - normalSeconds;
            if (currentDelta < bestDelta)
            {
                bestDelta = currentDelta;
                bestStart = currentStart;
                bestEndExclusive = floor + 1;
            }
        }

        double bestDuration = timing.Duration + bestDelta;
        return new MissingTwirlIntervalProbeResult(
            bestStart,
            bestEndExclusive,
            timing.Duration,
            bestDuration,
            bestDelta,
            -bestDelta);
    }

    private static int[] BuildPlanetCounts(LevelDocument level, TimingMap timing, int floorCount)
    {
        var planets = new int[floorCount];
        int currentPlanets = 2;

        for (int floor = 0; floor < floorCount; floor++)
        {
            if (level.ActionsByFloor.TryGetValue(floor, out LevelAction[]? actions))
            {
                foreach (LevelAction action in actions)
                {
                    if (!action.Active ||
                        !string.Equals(action.EventType, "MultiPlanet", StringComparison.Ordinal))
                        continue;

                    if (string.Equals(action.Planets, "ThreePlanets", StringComparison.OrdinalIgnoreCase) ||
                        action.Planets == "3")
                    {
                        currentPlanets = 3;
                    }
                    else if (string.Equals(action.Planets, "TwoPlanets", StringComparison.OrdinalIgnoreCase) ||
                             action.Planets == "2")
                    {
                        currentPlanets = 2;
                    }

                    if (floor > 0 && timing.Floors[floor - 1].MidSpin)
                        planets[floor - 1] = currentPlanets;
                }
            }

            planets[floor] = currentPlanets;
        }

        return planets;
    }

    private static double GetFlippedFloorSeconds(
        IReadOnlyList<FloorTiming> floors,
        int floor,
        int numPlanets)
    {
        FloorTiming current = floors[floor];
        bool flippedIsCcw = !current.IsCcw;
        double inverse = GetInverseAnglePerBeatMultiplanet(numPlanets);
        double offset = inverse * (flippedIsCcw ? -1.0 : 1.0);

        if (current.MidSpin)
            offset = 0.0;

        bool previousMidSpin = floor > 0 && floors[floor - 1].MidSpin;
        if (previousMidSpin && numPlanets > 2)
        {
            offset -= (TwoPi + inverse) * (flippedIsCcw ? -1.0 : 1.0);
        }

        double moved = GetAngleMoved(
            current.EntryAngle + offset,
            current.ExitAngle + (current.MidSpin ? offset : 0.0),
            !flippedIsCcw);

        if (moved <= 1e-6 || moved >= 6.283184482025146)
            moved = current.MidSpin ? 0.0 : TwoPi;

        double bpm = Math.Max(0.000001, current.Bpm);
        return moved / PiStock * (60.0 / bpm);
    }

    private static double GetAngleMoved(double entryAngle, double exitAngle, bool isCw)
    {
        double sign = isCw ? 1.0 : -1.0;
        return Mod((exitAngle - entryAngle) * sign, TwoPi);
    }

    private static double GetInverseAnglePerBeatMultiplanet(double planets)
    {
        return 3.1415926 * (planets - 2.0) / planets;
    }

    private static double Mod(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0.0 ? result + modulus : result;
    }
}
