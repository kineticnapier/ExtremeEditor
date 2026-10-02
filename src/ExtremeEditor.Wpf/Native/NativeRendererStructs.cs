using System.Runtime.InteropServices;
using ExtremeEditor.Rendering;

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
    public uint SpriteMetadataSize;
    public uint StaticDecorationSize;
    public uint FloorIconStateSize;
    public uint TrackVisualEventSize;
    public uint TrackAnimationSegmentSize;
    public uint TrackAnimationTimingSize;
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
    public float TransformRotation;
    public float TransformOpacity;
    public uint TrackTransformFlags;
    public float TrackExtendAnim;
    public int TrackSortingOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFloorIconState
{
    public static NativeFloorIconState None => new() { IconId = NativeFloor.NoIcon };

    public uint IconId;
    public uint IconFlags;
    public float IconAngle;

    public readonly void ApplyTo(ref NativeFloor floor)
    {
        floor.IconId = IconId;
        floor.IconFlags = IconFlags;
        floor.IconAngle = IconAngle;
    }
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
internal struct NativeStaticDecoration
{
    public const uint RelativeGlobal = 0u;
    public const uint RelativeTile = 1u;
    public const uint RelativeCamera = 2u;
    public const uint RelativeCameraAspect = 3u;
    public const uint RelativeRedPlanet = 4u;
    public const uint RelativeBluePlanet = 5u;
    public const uint RelativeGreenPlanet = 6u;
    public const uint FlagVisible = 1u;
    public const uint FlagStickToFloor = 2u;
    public const uint FlagLockRotation = 4u;
    public const uint FlagLockScale = 8u;

    public int SourceIndex;
    public int Floor;
    public uint AssetId;
    public uint RelativeMode;
    public float PositionX;
    public float PositionY;
    public float PivotOffsetX;
    public float PivotOffsetY;
    public float RotationRadians;
    public float ScaleX;
    public float ScaleY;
    public uint Color;
    public float Opacity;
    public int Depth;
    public uint Flags;
    public float BaseAnchorX;
    public float BaseAnchorY;
    public float ParallaxX;
    public float ParallaxY;
    public float ParallaxOffsetX;
    public float ParallaxOffsetY;
    public float ScaleMultiplier;
    public float ChartPositionX;
    public float ChartPositionY;

    public readonly string RelativeTo => RelativeMode switch
    {
        RelativeTile => "Tile",
        RelativeCamera => "Camera",
        RelativeCameraAspect => "CameraAspect",
        RelativeRedPlanet => "RedPlanet",
        RelativeBluePlanet => "BluePlanet",
        RelativeGreenPlanet => "GreenPlanet",
        _ => "Global"
    };
    public readonly bool Visible => (Flags & FlagVisible) != 0u;
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
internal struct NativeTrackVisualEvent
{
    public double StartTime;
    public double TransitionDuration;
    public int StartFloor;
    public int EndFloor;
    public uint GapLength;
    public uint PrimaryColor;
    public uint SecondaryColor;
    public uint VisualFlags;
    public float AnimDuration;
    public float GlowIntensity;
    public uint PulseLength;
    public uint Ease;
    public int SourceIndex;
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
    public double TrackTotalMilliseconds;
    public double TrackClockMilliseconds;
    public double TrackEvaluateMilliseconds;
    public double TrackApplyMilliseconds;
    public double TrackSpatialRemoveMilliseconds;
    public double TrackSpatialInsertMilliseconds;
    public uint VisibleCandidates;
    public uint FloorDraws;
    public uint IconDraws;
    public uint DrawCalls;
    public uint TrackTotalCount;
    public uint TrackActiveCount;
    public uint TrackAdmittedCount;
    public uint TrackFinishedCount;
    public ulong TrackEventScanCount;
    public ulong TrackMaxEventScanCount;
    public uint TrackActivePositionCount;
    public uint TrackActiveVisualOnlyCount;
    public uint TrackActiveSingleEventCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSpriteMetadata
{
    public const uint FlagValid = 1u;

    public uint StructSize;
    public uint Flags;
    public float SpriteRectX;
    public float SpriteRectY;
    public float SpriteRectWidth;
    public float SpriteRectHeight;
    public float TextureRectX;
    public float TextureRectY;
    public float TextureRectWidth;
    public float TextureRectHeight;
    public float PivotX;
    public float PivotY;
    public float PixelsPerUnit;

    public static NativeSpriteMetadata Missing => new()
    {
        StructSize = checked((uint)Marshal.SizeOf<NativeSpriteMetadata>())
    };

    public static NativeSpriteMetadata From(SpriteAssetMetadata? metadata)
    {
        NativeSpriteMetadata result = Missing;
        if (metadata is null)
            return result;

        result.Flags = FlagValid;
        result.SpriteRectX = metadata.SpriteRectX;
        result.SpriteRectY = metadata.SpriteRectY;
        result.SpriteRectWidth = metadata.SpriteRectWidth;
        result.SpriteRectHeight = metadata.SpriteRectHeight;
        result.TextureRectX = metadata.TextureRectX;
        result.TextureRectY = metadata.TextureRectY;
        result.TextureRectWidth = metadata.TextureRectWidth;
        result.TextureRectHeight = metadata.TextureRectHeight;
        result.PivotX = metadata.PivotX;
        result.PivotY = metadata.PivotY;
        result.PixelsPerUnit = metadata.PixelsPerUnit;
        return result;
    }
}

internal sealed record NativeIconAsset(uint Id, string ImagePath, string? OutlinePath)
{
    public float SpriteRectX { get; init; }
    public float SpriteRectY { get; init; }
    public float SpriteRectWidth { get; init; }
    public float SpriteRectHeight { get; init; }
    public float TextureRectX { get; init; }
    public float TextureRectY { get; init; }
    public float TextureRectWidth { get; init; }
    public float TextureRectHeight { get; init; }
    public float PivotX { get; init; }
    public float PivotY { get; init; }
    public float PixelsPerUnit { get; init; }
    public NativeSpriteMetadata ImageMetadata { get; init; } = NativeSpriteMetadata.Missing;
    public NativeSpriteMetadata OutlineMetadata { get; init; } = NativeSpriteMetadata.Missing;

    public static NativeIconAsset Create(uint id, string imagePath, string? outlinePath)
    {
        IconAssetCache.TryReadSpriteMetadata(imagePath, out SpriteAssetMetadata? imageMetadata);
        SpriteAssetMetadata? outlineMetadata = null;
        if (outlinePath is not null)
            IconAssetCache.TryReadSpriteMetadata(outlinePath, out outlineMetadata);

        return new NativeIconAsset(id, imagePath, outlinePath)
        {
            SpriteRectX = imageMetadata?.SpriteRectX ?? 0f,
            SpriteRectY = imageMetadata?.SpriteRectY ?? 0f,
            SpriteRectWidth = imageMetadata?.SpriteRectWidth ?? 0f,
            SpriteRectHeight = imageMetadata?.SpriteRectHeight ?? 0f,
            TextureRectX = imageMetadata?.TextureRectX ?? 0f,
            TextureRectY = imageMetadata?.TextureRectY ?? 0f,
            TextureRectWidth = imageMetadata?.TextureRectWidth ?? 0f,
            TextureRectHeight = imageMetadata?.TextureRectHeight ?? 0f,
            PivotX = imageMetadata?.PivotX ?? 0f,
            PivotY = imageMetadata?.PivotY ?? 0f,
            PixelsPerUnit = imageMetadata?.PixelsPerUnit ?? 0f,
            ImageMetadata = NativeSpriteMetadata.From(imageMetadata),
            OutlineMetadata = NativeSpriteMetadata.From(outlineMetadata)
        };
    }
}

internal sealed record NativeDecorationAsset(uint Id, string ImagePath);
