using System.Numerics;

namespace ExtremeEditor.Core;

public static class PathBuilder
{
    public static Vector2[] BuildPositions(ReadOnlySpan<double> angles)
    {
        if (angles.Length == 0)
            return [Vector2.Zero];

        // ADOFAI angleData describes the direction of path segments.
        // This is sufficient for the geometry-focused prototype.
        // Full compatibility (including special values/midspins) belongs in
        // the format layer rather than the renderer.
        // ADOFAI creates floor 0 plus one floor for every angle entry.
        var positions = new Vector2[angles.Length + 1];
        Vector2 current = Vector2.Zero;

        for (int i = 0; i < angles.Length; i++)
        {
            double angle = angles[i];

            // 999 is historically used as a special/midspin-like value by
            // some ADOFAI tooling. Keep the point stationary for now instead
            // of producing meaningless coordinates.
            if (Math.Abs(angle - 999.0) < 0.000001)
            {
                positions[i + 1] = current;
                continue;
            }

            // Mirrors scrLevelMaker exactly:
            //   exitAngle = (-angle + 90) degrees
            //   scrMisc.getVectorFromAngle(a, r) = (sin(a)*r, cos(a)*r)
            // Therefore the resulting unit step is (cos(angle), sin(angle)).
            double radians = (90.0 - angle) * Math.PI / 180.0;
            current += new Vector2(
                (float)Math.Sin(radians),
                (float)Math.Cos(radians));
            positions[i + 1] = current;
        }

        return positions;
    }

    public static WorldRect CalculateBounds(ReadOnlySpan<Vector2> positions)
    {
        if (positions.Length == 0)
            return new WorldRect(-1, -1, 1, 1);

        float minX = positions[0].X;
        float maxX = minX;
        float minY = positions[0].Y;
        float maxY = minY;

        for (int i = 1; i < positions.Length; i++)
        {
            Vector2 p = positions[i];
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        if (maxX - minX < 1f) { minX -= .5f; maxX += .5f; }
        if (maxY - minY < 1f) { minY -= .5f; maxY += .5f; }

        return new WorldRect(minX, minY, maxX, maxY);
    }
}
