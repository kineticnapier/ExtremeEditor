using System.Runtime.InteropServices;

namespace ExtremeEditor.Wpf.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAbiInfo
{
    public uint StructSize;
    public uint ApiVersion;
    public uint FloorSize;
    public uint ClockSize;
    public uint DiagnosticsSize;
    public uint TrackTransformEventSize;
    public uint CameraEventSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRendererCreateInfo
{
    public uint StructSize;
    public uint Width;
    public uint Height;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFloor
{
    public const uint NoIcon = uint.MaxValue;
    public const uint IconFlagFloor = 1u;
    public const uint IconFlagFlipped = 2u;
    public const uint TransformFlagEnabled = 1u;
    public const uint TransformFlagStickToFloors = 2u;

    public float X;
    public float Y;
    public float EntryAngle;
    public uint GeometryId;
    public uint IconId;
    public uint IconFlags;
    public float IconAngle;
    public uint TrackPrimaryColor;
    public uint TrackSecondaryColor;
    public uint TrackVisualFlags;
    public float TrackAnimDuration;
    public float TrackGlowIntensity;
    public int TrackStartFloor;
    public uint TrackPulseLength;
    public float TransformScaleX;
    public float TransformScaleY;
    public float TransformOpacity;
    public uint TrackTransformFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeGeometry
{
    public uint PointOffset;
    public uint PointCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePlaybackTiming
{
    public const uint FlagCcw = 1u;

    public double EntryTime;
    public double ExitTime;
    public double PauseSeconds;
    public float EntryAngle;
    public float AngleMoved;
    public uint Flags;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCameraEvent
{
    public const uint FlagTargetPlayerX = 1u;
    public const uint FlagTargetPlayerY = 2u;
    public const uint FlagReferenceTile = 4u;
    public const uint FlagApplyX = 0x100u;
    public const uint FlagApplyY = 0x200u;
    public const uint FlagApplyRotation = 0x400u;
    public const uint FlagApplyZoom = 0x800u;
    public const uint FlagApplyMask = FlagApplyX | FlagApplyY | FlagApplyRotation | FlagApplyZoom;

    public const uint EaseLinear = 0u;
    public const uint EaseInSine = 1u;
    public const uint EaseOutSine = 2u;
    public const uint EaseInOutSine = 3u;
    public const uint EaseInQuad = 4u;
    public const uint EaseOutQuad = 5u;
    public const uint EaseInOutQuad = 6u;
    public const uint EaseInCubic = 7u;
    public const uint EaseOutCubic = 8u;
    public const uint EaseInOutCubic = 9u;
    public const uint EaseInQuart = 10u;
    public const uint EaseOutQuart = 11u;
    public const uint EaseInOutQuart = 12u;
    public const uint EaseInQuint = 13u;
    public const uint EaseOutQuint = 14u;
    public const uint EaseInOutQuint = 15u;
    public const uint EaseInExpo = 16u;
    public const uint EaseOutExpo = 17u;
    public const uint EaseInOutExpo = 18u;
    public const uint EaseInCirc = 19u;
    public const uint EaseOutCirc = 20u;
    public const uint EaseInOutCirc = 21u;
    public const uint EaseInBack = 22u;
    public const uint EaseOutBack = 23u;
    public const uint EaseInOutBack = 24u;
    public const uint EaseInElastic = 25u;
    public const uint EaseOutElastic = 26u;
    public const uint EaseInOutElastic = 27u;
    public const uint EaseInBounce = 28u;
    public const uint EaseOutBounce = 29u;
    public const uint EaseInOutBounce = 30u;

    public double StartTime;
    public double DurationSeconds;
    public float StartX;
    public float StartY;
    public float TargetX;
    public float TargetY;
    public float StartRotation;
    public float TargetRotation;
    public float StartZoom;
    public float TargetZoom;
    public uint Flags;
    public uint Ease;
    public int ReferenceFloor;
    public uint ReferenceFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTrackTransformEvent
{
    public const uint FlagX = 1u << 0;
    public const uint FlagY = 1u << 1;
    public const uint FlagRotation = 1u << 2;
    public const uint FlagScaleX = 1u << 3;
    public const uint FlagScaleY = 1u << 4;
    public const uint FlagOpacity = 1u << 5;

    public double StartTime;
    public double DurationSeconds;
    public int Floor;
    public uint Flags;
    public float StartX;
    public float StartY;
    public float TargetX;
    public float TargetY;
    public float StartRotation;
    public float TargetRotation;
    public float StartScaleX;
    public float StartScaleY;
    public float TargetScaleX;
    public float TargetScaleY;
    public float StartOpacity;
    public float TargetOpacity;
    public uint Ease;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRendererDiagnostics
{
    public uint StructSize;
    public uint Reserved;
    public double Fps;
    public double FrameMilliseconds;
    public double MaxFrameMilliseconds;
    public double RenderMilliseconds;
    public double CullMilliseconds;
    public double UpdateMilliseconds;
    public double DrawSetupMilliseconds;
    public double FloorMilliseconds;
    public double IconMilliseconds;
    public double OverlayMilliseconds;
    public double EndDrawMilliseconds;
    public double PresentMilliseconds;
    public uint VisibleCandidates;
    public uint FloorDraws;
    public uint IconDraws;
    public uint DrawCalls;
}

internal sealed record NativeIconAsset(
    uint Id,
    string ImagePath,
    string? OutlinePath);
