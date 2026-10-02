using System.Runtime.InteropServices;

namespace ExtremeEditor.Wpf.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTrackAnimationSegment
{
    public const uint FlagAppearPropertyEnabled = 1u;
    public const uint FlagDisappearPropertyEnabled = 2u;
    public const uint AppearNone = 0u;
    public const uint AppearAssemble = 1u;
    public const uint AppearAssembleFar = 2u;
    public const uint AppearExtend = 3u;
    public const uint AppearGrow = 4u;
    public const uint AppearGrowSpin = 5u;
    public const uint AppearFade = 6u;
    public const uint AppearDrop = 7u;
    public const uint AppearRise = 8u;

    public const uint DisappearNone = 0u;
    public const uint DisappearScatter = 1u;
    public const uint DisappearScatterFar = 2u;
    public const uint DisappearRetract = 3u;
    public const uint DisappearShrink = 4u;
    public const uint DisappearShrinkSpin = 5u;
    public const uint DisappearFade = 6u;

    public int StartFloor;
    public int EndFloor;
    public uint AppearType;
    public uint DisappearType;
    public float BeatsAhead;
    public float BeatsBehind;
    public float AppearReferenceSpeed;
    public float DisappearReferenceSpeed;
    public float Pitch;
    public int SourceIndex;
    public uint Flags;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTrackAnimationTiming
{
    public double EntryTime;
    public float BeatSecondsNoPitch;
    public float Speed;
}
