using System.Numerics;

namespace ExtremeEditor.Core;

/// <summary>
/// Uniform spatial index for viewport culling.
/// The renderer asks for only the cells intersecting the current viewport.
/// </summary>
public sealed class SpatialGridIndex
{
    private readonly Dictionary<long, int[]> _cells;
    private readonly float _cellSize;

    public SpatialGridIndex(ReadOnlySpan<Vector2> positions, float cellSize = 32f)
    {
        _cellSize = Math.Max(0.25f, cellSize);
        var build = new Dictionary<long, List<int>>();

        for (int i = 0; i < positions.Length; i++)
        {
            (int x, int y) = CellOf(positions[i]);
            long key = Key(x, y);
            if (!build.TryGetValue(key, out var list))
            {
                list = new List<int>(16);
                build.Add(key, list);
            }
            list.Add(i);
        }

        _cells = new Dictionary<long, int[]>(build.Count);
        foreach (var pair in build)
            _cells.Add(pair.Key, pair.Value.ToArray());
    }

    public int CellCount => _cells.Count;

    public void Query(WorldRect bounds, List<int> output)
    {
        output.Clear();

        int minX = FastFloor(bounds.Left / _cellSize);
        int maxX = FastFloor(bounds.Right / _cellSize);
        int minY = FastFloor(bounds.Top / _cellSize);
        int maxY = FastFloor(bounds.Bottom / _cellSize);

        // Guard against pathological zoom-out queries. At that point drawing
        // every individual floor is visually useless anyway.
        long cellsWide = (long)maxX - minX + 1;
        long cellsHigh = (long)maxY - minY + 1;
        if (cellsWide <= 0 || cellsHigh <= 0 || cellsWide * cellsHigh > 2_000_000)
            return;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (_cells.TryGetValue(Key(x, y), out int[]? indices))
                    output.AddRange(indices);
            }
        }
    }

    private (int X, int Y) CellOf(Vector2 p) =>
        (FastFloor(p.X / _cellSize), FastFloor(p.Y / _cellSize));

    private static int FastFloor(float value)
    {
        int i = (int)value;
        return value < i ? i - 1 : i;
    }

    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
}
