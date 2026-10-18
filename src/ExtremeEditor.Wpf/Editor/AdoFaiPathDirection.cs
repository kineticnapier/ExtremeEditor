namespace ExtremeEditor.Wpf;

internal static class AdoFaiPathDirection
{
    private const double Midspin = 999.0;
    private const double Epsilon = 0.000001;

    internal static bool PointsBackwards(
        IReadOnlyList<double> angles,
        int floor,
        double direction)
    {
        if (!TryGetBackwardDirection(angles, floor, out double backwards))
            return false;

        double delta = Math.Abs(Normalize(direction - backwards));
        delta = Math.Min(delta, 360.0 - delta);
        return delta <= 0.0001;
    }

    internal static bool TryGetBackwardDirection(
        IReadOnlyList<double> angles,
        int floor,
        out double backwards)
    {
        backwards = 0.0;
        if (floor <= 0 || floor - 1 >= angles.Count)
            return false;

        int index = floor - 1;
        int trailingMidspins = 0;
        while (index >= 0 && IsMidspin(angles[index]))
        {
            trailingMidspins++;
            index--;
        }

        // A malformed path with no preceding direction has no safe deletion
        // direction. Preserve the existing no-op behavior in that case.
        if (index < 0)
            return false;

        double previousDirection = Normalize(angles[index]);
        backwards = Normalize(previousDirection +
            ((trailingMidspins & 1) == 0 ? 180.0 : 0.0));
        return true;
    }

    // The eight native HUD buttons are Q,W,E,A,D,Z,S,C in that order.
    // Only highlight a deletion button when the exact reverse direction is
    // available; arbitrary angles and malformed Midspin paths have none.
    internal static int GetBackwardHudButtonIndex(
        IReadOnlyList<double> angles,
        int floor)
    {
        if (!TryGetBackwardDirection(angles, floor, out double backwards) ||
            !double.IsFinite(backwards))
            return -1;

        ReadOnlySpan<double> buttonAngles = [135.0, 90.0, 45.0, 180.0,
                                             0.0, 225.0, 270.0, 315.0];
        for (int i = 0; i < buttonAngles.Length; i++)
        {
            double delta = Math.Abs(Normalize(backwards - buttonAngles[i]));
            if (Math.Min(delta, 360.0 - delta) <= 0.0001)
                return i;
        }
        return -1;
    }

    private static bool IsMidspin(double angle) =>
        Math.Abs(angle - Midspin) < Epsilon;

    private static double Normalize(double angle)
    {
        double result = angle % 360.0;
        return result < 0.0 ? result + 360.0 : result;
    }
}
