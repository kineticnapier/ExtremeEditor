namespace ExtremeEditor.Core;

public sealed record MissingTwirlCandidate(
    int Floor,
    double DurationSeconds,
    double AbsoluteErrorSeconds);

public sealed record MissingTwirlProbeResult(
    int BestFloor,
    double CurrentDurationSeconds,
    double TargetDurationSeconds,
    double BestDurationSeconds,
    double BestAbsoluteErrorSeconds,
    IReadOnlyList<MissingTwirlCandidate> Candidates);

/// <summary>
/// Diagnostic probe for a single missing Twirl. Inserting one Twirl at floor k
/// flips CW/CCW parity for every floor from k onward. The probe precomputes the
/// duration of every floor under the opposite direction, then evaluates all k
/// with prefix(normal) + suffix(flipped).
/// </summary>
public static class MissingTwirlProbe
{
    private const double PiStock = 3.1415927410125732;
    private const double TwoPi = 6.2831854820251465;

    public static MissingTwirlProbeResult Analyze(
        LevelDocument level,
        TimingMap timing,
        double targetDurationSeconds,
        int topCandidateCount = 12)
    {
        int floorCount = Math.Min(level.FloorCount, timing.Floors.Count);
        if (floorCount <= 1)
        {
            return new MissingTwirlProbeResult(
                -1,
                timing.Duration,
                targetDurationSeconds,
                timing.Duration,
                Math.Abs(timing.Duration - targetDurationSeconds),
                []);
        }

        int[] planets = BuildPlanetCounts(level, timing, floorCount);
        var prefixNormal = new double[floorCount + 1];
        var suffixFlipped = new double[floorCount + 1];

        for (int floor = 0; floor < floorCount; floor++)
        {
            FloorTiming current = timing.Floors[floor];
            prefixNormal[floor + 1] = prefixNormal[floor] +
                (current.ExitTime - current.EntryTime);
        }

        for (int floor = floorCount - 1; floor >= 1; floor--)
        {
            suffixFlipped[floor] = suffixFlipped[floor + 1] +
                                   GetFlippedFloorSeconds(
                                       timing.Floors,
                                       floor,
                                       planets[floor]);
        }

        int bestFloor = 1;
        double bestDuration = prefixNormal[1] + suffixFlipped[1];
        double bestError = Math.Abs(bestDuration - targetDurationSeconds);
        int keepCount = Math.Max(1, topCandidateCount);
        var top = new List<MissingTwirlCandidate>(keepCount);

        for (int floor = 1; floor < floorCount; floor++)
        {
            double duration = prefixNormal[floor] + suffixFlipped[floor];
            double error = Math.Abs(duration - targetDurationSeconds);
            if (error < bestError)
            {
                bestFloor = floor;
                bestDuration = duration;
                bestError = error;
            }

            InsertCandidate(top, new MissingTwirlCandidate(floor, duration, error), keepCount);
        }

        return new MissingTwirlProbeResult(
            bestFloor,
            timing.Duration,
            targetDurationSeconds,
            bestDuration,
            bestError,
            top);
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

    private static void InsertCandidate(
        List<MissingTwirlCandidate> candidates,
        MissingTwirlCandidate candidate,
        int maxCount)
    {
        int index = candidates.BinarySearch(
            candidate,
            Comparer<MissingTwirlCandidate>.Create((left, right) =>
            {
                int byError = left.AbsoluteErrorSeconds.CompareTo(right.AbsoluteErrorSeconds);
                return byError != 0 ? byError : left.Floor.CompareTo(right.Floor);
            }));
        if (index < 0)
            index = ~index;

        if (index >= maxCount && candidates.Count >= maxCount)
            return;

        candidates.Insert(index, candidate);
        if (candidates.Count > maxCount)
            candidates.RemoveAt(candidates.Count - 1);
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
