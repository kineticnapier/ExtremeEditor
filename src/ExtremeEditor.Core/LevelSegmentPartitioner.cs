namespace ExtremeEditor.Core;

/// <summary>
/// A floor-aligned, independently editable slice of a chart.
/// The segment owns the angle that enters its first floor (except floor zero),
/// so concatenating DestinationAngles reconstructs the original angleData
/// without inventing a bridge tile.
/// </summary>
public sealed record LevelSegment(
    int StartFloor,
    int EndFloorExclusive,
    double[] DestinationAngles,
    LevelAction[] Actions,
    LevelDecoration[] Decorations)
{
    public int FloorCount => EndFloorExclusive - StartFloor;
}

/// <summary>
/// Prototype boundary for lossless floor-aligned partitioning and reassembly.
/// No time-based slicing, collaborative merge or Git integration is implied.
/// </summary>
public static class LevelSegmentPartitioner
{
    public static IReadOnlyList<LevelSegment> Split(
        LevelDocument source, params int[] cutFloors)
    {
        throw new NotImplementedException(
            "RED: lossless LevelDocument -> independent LevelSegments is not implemented.");
    }

    public static LevelDocument Merge(
        LevelDocument template, IReadOnlyList<LevelSegment> segments)
    {
        throw new NotImplementedException(
            "RED: LevelSegments -> LevelDocument reassembly is not implemented.");
    }
}
