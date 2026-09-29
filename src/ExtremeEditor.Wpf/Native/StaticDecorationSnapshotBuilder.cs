using System.Globalization;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct StaticDecorationSnapshotData(
    NativeStaticDecoration[] Instances,
    NativeDecorationAsset[] Assets,
    NativeDecorationPlaybackRuntime? PlaybackRuntime);

internal sealed class NativeDecorationPlaybackRuntime
{
    private readonly NativeStaticDecoration[] _instances;
    private readonly AnimatedDecorationBinding[] _animated;
    private double _lastTime = double.NaN;

    internal NativeDecorationPlaybackRuntime(
        NativeStaticDecoration[] instances,
        AnimatedDecorationBinding[] animated)
    {
        _instances = instances;
        _animated = animated;
    }

    internal int AnimatedCount => _animated.Length;

    internal bool Update(double chartTime)
    {
        if (_animated.Length == 0 || chartTime.Equals(_lastTime))
            return false;

        foreach (AnimatedDecorationBinding binding in _animated)
        {
            DecorationState state = DecorationState.Evaluate(
                binding.Decoration,
                binding.Timeline,
                chartTime);
            _instances[binding.InstanceIndex] = StaticDecorationSnapshotBuilder.ApplyState(
                binding.Template,
                binding.Decoration,
                state);
        }

        _lastTime = chartTime;
        return true;
    }
}

internal readonly record struct AnimatedDecorationBinding(
    int InstanceIndex,
    LevelDecoration Decoration,
    VfxTimeline Timeline,
    NativeStaticDecoration Template);

internal static class StaticDecorationSnapshotBuilder
{
    private const float DegreesToRadians = MathF.PI / 180f;

    // Native API v18 has four low flag bits in use. Masking is packed into the
    // next bits without widening the hot decoration ABI. For Mask instances only,
    // ChartPositionX/Y carry the masking front/back depth; Mask decorations are
    // never rendered as ordinary sprites, so their diagnostic chart-position slots
    // are otherwise unused by the renderer.
    internal const uint MaskingTypeBits = 0x300u;
    internal const uint MaskingNone = 0x000u;
    internal const uint MaskingMask = 0x100u;
    internal const uint MaskingVisibleInside = 0x200u;
    internal const uint MaskingVisibleOutside = 0x300u;
    internal const uint MaskingUseDepth = 0x400u;

    internal static StaticDecorationSnapshotData Build(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        VfxTimeline timeline = HasMoveDecorations(level)
            ? VfxTimelineBuilder.Build(level)
            : new VfxTimeline(Array.Empty<VfxOccurrence>(), Array.Empty<VfxRepeatDescriptor>());
        return BuildCore(level, timeline, double.NegativeInfinity);
    }

    internal static StaticDecorationSnapshotData Build(
        LevelDocument level,
        VfxTimeline timeline,
        double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timeline);
        return BuildCore(level, timeline, timeSeconds);
    }

    private static StaticDecorationSnapshotData BuildCore(
        LevelDocument level,
        VfxTimeline timeline,
        double timeSeconds)
    {
        Dictionary<string, List<(int Order, VfxOccurrence Occurrence)>> movesByTag =
            BuildMoveIndex(timeline);

        var instances = new List<NativeStaticDecoration>();
        var assets = new List<NativeDecorationAsset>();
        var animated = new List<AnimatedDecorationBinding>();
        var assetIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        string? levelDirectory = level.SourcePath == "<synthetic>"
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(level.SourcePath));

        int addDecorationCount = 0;
        int acceptedCount = 0;
        int excludedNonAddDecoration = 0;
        int excludedInvisible = 0;
        int excludedOpacity = 0;
        int excludedMissingImage = 0;
        int excludedUnsupportedPlacement = 0;
        int excludedInvalidFloor = 0;
        int placementTile = 0;
        int placementGlobal = 0;
        int placementCamera = 0;
        int placementCameraAspect = 0;
        int placementRedPlanet = 0;
        int placementBluePlanet = 0;
        int placementGreenPlanet = 0;
        int placementOther = 0;
        int opacityZeroTotal = 0;
        int opacityZeroAnimated = 0;
        int opacityZeroStatic = 0;
        int decorationsWithTag = 0;
        int decorationsMatchedByMove = 0;
        int taggedMoveOccurrenceCount = 0;
        int scaleZeroTotal = 0;
        int scaleZeroSourceHasScale = 0;
        int scaleZeroSourceMissingScale = 0;
        int scaleZeroAnimated = 0;
        int scaleZeroStatic = 0;
        int scaleMissingTotal = 0;
        int scaleMissingNativeZero = 0;
        int maskCount = 0;
        int visibleInsideMaskCount = 0;
        int visibleOutsideMaskCount = 0;
        string? scaleZeroExample = null;

        foreach (VfxOccurrence occurrence in timeline.Occurrences)
        {
            if (IsIndexedMoveOccurrence(occurrence))
                taggedMoveOccurrenceCount++;
        }

        int moveDecorationsActionCount = level.ActionTypeCounts.TryGetValue(
            "MoveDecorations",
            out int moveActionCount)
            ? moveActionCount
            : 0;

        foreach (LevelDecoration decoration in level.Decorations)
        {
            if (!string.Equals(decoration.EventType, "AddDecoration", StringComparison.Ordinal))
            {
                excludedNonAddDecoration++;
                continue;
            }

            addDecorationCount++;

            JsonObject properties = decoration.Properties;
            string? relativeTo = ReadString(properties["relativeTo"]);
            relativeTo = string.IsNullOrWhiteSpace(relativeTo) ? "Tile" : relativeTo.Trim();
            CountPlacement(
                relativeTo,
                ref placementTile,
                ref placementGlobal,
                ref placementCamera,
                ref placementCameraAspect,
                ref placementRedPlanet,
                ref placementBluePlanet,
                ref placementGreenPlanet,
                ref placementOther);

            string? decorationTags = ReadString(properties["tag"]);
            if (HasTags(decorationTags))
                decorationsWithTag++;
            VfxOccurrence[] relevantMoves = ResolveMoves(properties["tag"], movesByTag);
            bool isAnimated = relevantMoves.Length > 0;
            if (isAnimated)
                decorationsMatchedByMove++;

            float opacity = Math.Clamp(ReadFloat(properties["opacity"], 100f) / 100f, 0f, 1f);
            if (opacity <= 0f)
            {
                opacityZeroTotal++;
                if (isAnimated)
                    opacityZeroAnimated++;
                else
                    opacityZeroStatic++;
            }

            if (!ReadBool(properties["visible"], defaultValue: true))
            {
                excludedInvisible++;
                continue;
            }

            if (opacity <= 0f && !isAnimated)
            {
                excludedOpacity++;
                continue;
            }

            uint relativeMode = ResolveRelativeMode(relativeTo);
            if (relativeMode == NativeStaticDecoration.RelativeTile)
            {
                if (decoration.Floor is not int floor || (uint)floor >= (uint)level.Positions.Length)
                {
                    excludedInvalidFloor++;
                    continue;
                }
            }
            else if (relativeMode == uint.MaxValue)
            {
                excludedUnsupportedPlacement++;
                continue;
            }

            string? image = ReadString(properties["decorationImage"]);
            if (string.IsNullOrWhiteSpace(image) || levelDirectory is null)
            {
                excludedMissingImage++;
                continue;
            }

            string imagePath;
            try
            {
                imagePath = Path.GetFullPath(Path.Combine(levelDirectory, image));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                excludedMissingImage++;
                continue;
            }

            if (!File.Exists(imagePath))
            {
                excludedMissingImage++;
                continue;
            }

            if (!assetIds.TryGetValue(imagePath, out uint assetId))
            {
                assetId = checked((uint)assets.Count);
                assetIds.Add(imagePath, assetId);
                assets.Add(new NativeDecorationAsset(assetId, imagePath));
            }

            DecorationState initialState = DecorationState.CreateInitial(decoration);
            DecorationRendererTransform rendererTransform =
                DecorationWorldTransformResolver.ResolveRendererTransform(decoration, initialState);
            bool sourceHasScale = properties.ContainsKey("scale");
            float scaleX = checked((float)initialState.ScaleX);
            float scaleY = checked((float)initialState.ScaleY);
            float rotationDegrees = checked((float)initialState.Rotation);
            int depth = ReadInt(properties["depth"], 0);
            uint color = initialState.Color;
            float baseAnchorX = 0f;
            float baseAnchorY = 0f;
            if (decoration.Floor is int anchorFloor &&
                (uint)anchorFloor < (uint)level.Positions.Length)
            {
                baseAnchorX = level.Positions[anchorFloor].X;
                baseAnchorY = level.Positions[anchorFloor].Y;
            }

            uint maskingType = ResolveMaskingType(ReadString(properties["maskingType"]));
            if (maskingType == MaskingMask)
                maskCount++;
            else if (maskingType == MaskingVisibleInside)
                visibleInsideMaskCount++;
            else if (maskingType == MaskingVisibleOutside)
                visibleOutsideMaskCount++;

            bool useMaskingDepth = ReadBool(properties["useMaskingDepth"], defaultValue: false);
            int maskingFrontDepth = ReadInt(properties["maskingFrontDepth"], -1);
            int maskingBackDepth = ReadInt(properties["maskingBackDepth"], -1);

            uint flags = NativeStaticDecoration.FlagVisible | maskingType;
            if (useMaskingDepth && maskingType == MaskingMask)
                flags |= MaskingUseDepth;
            if (rendererTransform.StickToFloor)
                flags |= NativeStaticDecoration.FlagStickToFloor;
            if (rendererTransform.LockRotation)
                flags |= NativeStaticDecoration.FlagLockRotation;
            if (rendererTransform.LockScale)
                flags |= NativeStaticDecoration.FlagLockScale;

            var template = new NativeStaticDecoration
            {
                SourceIndex = decoration.SourceIndex,
                Floor = decoration.Floor ?? -1,
                AssetId = assetId,
                RelativeMode = relativeMode,
                PositionX = rendererTransform.Position.X,
                PositionY = rendererTransform.Position.Y,
                PivotOffsetX = rendererTransform.PivotOffset.X,
                PivotOffsetY = rendererTransform.PivotOffset.Y,
                RotationRadians = rotationDegrees * DegreesToRadians,
                ScaleX = scaleX / 100f,
                ScaleY = scaleY / 100f,
                Color = color,
                Opacity = Math.Clamp(checked((float)(initialState.Opacity / 100.0)), 0f, 1f),
                Depth = depth,
                Flags = flags,
                BaseAnchorX = baseAnchorX,
                BaseAnchorY = baseAnchorY,
                ParallaxX = rendererTransform.Parallax.X,
                ParallaxY = rendererTransform.Parallax.Y,
                ParallaxOffsetX = rendererTransform.ParallaxOffset.X,
                ParallaxOffsetY = rendererTransform.ParallaxOffset.Y,
                ScaleMultiplier = rendererTransform.ScaleMultiplier,
                ChartPositionX = maskingType == MaskingMask
                    ? maskingFrontDepth
                    : checked((float)initialState.PositionX),
                ChartPositionY = maskingType == MaskingMask
                    ? maskingBackDepth
                    : checked((float)initialState.PositionY)
            };
            int instanceIndex = instances.Count;
            instances.Add(template);
            acceptedCount++;

            if (!sourceHasScale)
            {
                scaleMissingTotal++;
                if (Math.Abs(template.ScaleX) <= 0.000001f ||
                    Math.Abs(template.ScaleY) <= 0.000001f)
                {
                    scaleMissingNativeZero++;
                }
            }

            if (Math.Abs(template.ScaleX) <= 0.000001f ||
                Math.Abs(template.ScaleY) <= 0.000001f)
            {
                scaleZeroTotal++;
                if (sourceHasScale)
                    scaleZeroSourceHasScale++;
                else
                    scaleZeroSourceMissingScale++;
                if (isAnimated)
                    scaleZeroAnimated++;
                else
                    scaleZeroStatic++;

                scaleZeroExample ??=
                    $"[decoration-diagnostic] scaleZeroExample sourceIndex={decoration.SourceIndex} " +
                    $"floor={decoration.Floor?.ToString(CultureInfo.InvariantCulture) ?? "<none>"} " +
                    $"sourceHasScale={sourceHasScale} " +
                    $"rawScale={properties["scale"]?.ToJsonString() ?? "<missing>"} " +
                    $"parsedScale=({scaleX.ToString("G9", CultureInfo.InvariantCulture)}," +
                    $"{scaleY.ToString("G9", CultureInfo.InvariantCulture)}) " +
                    $"nativeScale=({template.ScaleX.ToString("G9", CultureInfo.InvariantCulture)}," +
                    $"{template.ScaleY.ToString("G9", CultureInfo.InvariantCulture)}) " +
                    $"moveTarget={isAnimated}";
            }

            if (isAnimated)
            {
                animated.Add(new AnimatedDecorationBinding(
                    instanceIndex,
                    decoration,
                    new VfxTimeline(relevantMoves, Array.Empty<VfxRepeatDescriptor>()),
                    template));
            }
        }

        NativeStaticDecoration[] instanceArray = instances.ToArray();
        NativeDecorationPlaybackRuntime? runtime = animated.Count == 0
            ? null
            : new NativeDecorationPlaybackRuntime(instanceArray, animated.ToArray());
        runtime?.Update(timeSeconds);

        if (DecorationDiagnostics.Enabled)
        {
            Console.WriteLine(
                $"[decoration-diagnostic] source={level.DecorationCount} addDecoration={addDecorationCount} " +
                $"accepted={acceptedCount} excludedNonAddDecoration={excludedNonAddDecoration} " +
                $"excludedInvisible={excludedInvisible} excludedOpacity={excludedOpacity} " +
                $"excludedMissingImage={excludedMissingImage} " +
                $"excludedUnsupportedPlacement={excludedUnsupportedPlacement} " +
                $"excludedInvalidFloor={excludedInvalidFloor}");
            Console.WriteLine(
                $"[decoration-diagnostic] placements Tile={placementTile} Global={placementGlobal} " +
                $"Camera={placementCamera} CameraAspect={placementCameraAspect} " +
                $"RedPlanet={placementRedPlanet} BluePlanet={placementBluePlanet} " +
                $"GreenPlanet={placementGreenPlanet} other={placementOther} " +
                $"managedSnapshotItems={instanceArray.Length} animated={animated.Count} assets={assets.Count}");
            Console.WriteLine(
                $"[decoration-diagnostic] opacityZeroTotal={opacityZeroTotal} " +
                $"opacityZeroAnimated={opacityZeroAnimated} opacityZeroStatic={opacityZeroStatic}");
            Console.WriteLine(
                $"[decoration-diagnostic] moveDecorationsActionCount={moveDecorationsActionCount} " +
                $"timelineOccurrenceCount={timeline.Occurrences.Count} " +
                $"taggedMoveOccurrenceCount={taggedMoveOccurrenceCount} " +
                $"decorationsWithTag={decorationsWithTag} " +
                $"decorationsMatchedByMove={decorationsMatchedByMove}");
            Console.WriteLine(
                $"[decoration-diagnostic] scaleZeroTotal={scaleZeroTotal} " +
                $"scaleZeroSourceHasScale={scaleZeroSourceHasScale} " +
                $"scaleZeroSourceMissingScale={scaleZeroSourceMissingScale} " +
                $"scaleZeroAnimated={scaleZeroAnimated} scaleZeroStatic={scaleZeroStatic} " +
                $"scaleMissingTotal={scaleMissingTotal} scaleMissingNativeZero={scaleMissingNativeZero}");
            Console.WriteLine(
                $"[decoration-diagnostic] masks={maskCount} visibleInsideMask={visibleInsideMaskCount} " +
                $"visibleOutsideMask={visibleOutsideMaskCount}");
            if (scaleZeroExample is not null)
                Console.WriteLine(scaleZeroExample);
        }

        return new StaticDecorationSnapshotData(instanceArray, assets.ToArray(), runtime);
    }

    private static void CountPlacement(
        string relativeTo,
        ref int tile,
        ref int global,
        ref int camera,
        ref int cameraAspect,
        ref int redPlanet,
        ref int bluePlanet,
        ref int greenPlanet,
        ref int other)
    {
        if (string.Equals(relativeTo, "Tile", StringComparison.OrdinalIgnoreCase))
            tile++;
        else if (string.Equals(relativeTo, "Global", StringComparison.OrdinalIgnoreCase))
            global++;
        else if (string.Equals(relativeTo, "Camera", StringComparison.OrdinalIgnoreCase))
            camera++;
        else if (string.Equals(relativeTo, "CameraAspect", StringComparison.OrdinalIgnoreCase))
            cameraAspect++;
        else if (string.Equals(relativeTo, "RedPlanet", StringComparison.OrdinalIgnoreCase))
            redPlanet++;
        else if (string.Equals(relativeTo, "BluePlanet", StringComparison.OrdinalIgnoreCase))
            bluePlanet++;
        else if (string.Equals(relativeTo, "GreenPlanet", StringComparison.OrdinalIgnoreCase))
            greenPlanet++;
        else
            other++;
    }

    internal static NativeStaticDecoration ApplyState(
        NativeStaticDecoration template,
        LevelDecoration decoration,
        DecorationState state)
    {
        DecorationRendererTransform rendererTransform =
            DecorationWorldTransformResolver.ResolveRendererTransform(decoration, state);
        template.PositionX = rendererTransform.Position.X;
        template.PositionY = rendererTransform.Position.Y;
        template.PivotOffsetX = rendererTransform.PivotOffset.X;
        template.PivotOffsetY = rendererTransform.PivotOffset.Y;
        template.ParallaxX = rendererTransform.Parallax.X;
        template.ParallaxY = rendererTransform.Parallax.Y;
        template.ParallaxOffsetX = rendererTransform.ParallaxOffset.X;
        template.ParallaxOffsetY = rendererTransform.ParallaxOffset.Y;
        template.ScaleMultiplier = rendererTransform.ScaleMultiplier;
        if ((template.Flags & MaskingTypeBits) != MaskingMask)
        {
            template.ChartPositionX = checked((float)state.PositionX);
            template.ChartPositionY = checked((float)state.PositionY);
        }
        template.RotationRadians = checked((float)state.Rotation) * DegreesToRadians;
        template.ScaleX = checked((float)(state.ScaleX / 100.0));
        template.ScaleY = checked((float)(state.ScaleY / 100.0));
        template.Opacity = Math.Clamp(checked((float)(state.Opacity / 100.0)), 0f, 1f);
        template.Color = state.Color;
        return template;
    }

    private static uint ResolveRelativeMode(string relativeTo)
    {
        if (string.Equals(relativeTo, "Tile", StringComparison.OrdinalIgnoreCase))
            return NativeStaticDecoration.RelativeTile;
        if (string.Equals(relativeTo, "Global", StringComparison.OrdinalIgnoreCase))
            return NativeStaticDecoration.RelativeGlobal;
        if (string.Equals(relativeTo, "Camera", StringComparison.OrdinalIgnoreCase))
            return NativeStaticDecoration.RelativeCamera;
        if (string.Equals(relativeTo, "CameraAspect", StringComparison.OrdinalIgnoreCase))
            return NativeStaticDecoration.RelativeCameraAspect;
        if (string.Equals(relativeTo, "RedPlanet", StringComparison.OrdinalIgnoreCase))
            return NativeStaticDecoration.RelativeRedPlanet;
        if (string.Equals(relativeTo, "BluePlanet", StringComparison.OrdinalIgnoreCase))
            return NativeStaticDecoration.RelativeBluePlanet;
        if (string.Equals(relativeTo, "GreenPlanet", StringComparison.OrdinalIgnoreCase))
            return NativeStaticDecoration.RelativeGreenPlanet;
        return uint.MaxValue;
    }

    private static uint ResolveMaskingType(string? maskingType)
    {
        if (string.Equals(maskingType, "Mask", StringComparison.OrdinalIgnoreCase))
            return MaskingMask;
        if (string.Equals(maskingType, "VisibleInsideMask", StringComparison.OrdinalIgnoreCase))
            return MaskingVisibleInside;
        if (string.Equals(maskingType, "VisibleOutsideMask", StringComparison.OrdinalIgnoreCase))
            return MaskingVisibleOutside;
        return MaskingNone;
    }

    private static Dictionary<string, List<(int Order, VfxOccurrence Occurrence)>> BuildMoveIndex(
        VfxTimeline timeline)
    {
        var result = new Dictionary<string, List<(int, VfxOccurrence)>>(StringComparer.Ordinal);
        for (int order = 0; order < timeline.Occurrences.Count; order++)
        {
            VfxOccurrence occurrence = timeline.Occurrences[order];
            if (!IsIndexedMoveOccurrence(occurrence))
                continue;

            foreach (string tag in MoveDecorationsTargeting.GetTargetTags(occurrence.SourceEvent))
            {
                if (!result.TryGetValue(tag, out List<(int, VfxOccurrence)>? entries))
                {
                    entries = [];
                    result.Add(tag, entries);
                }
                entries.Add((order, occurrence));
            }
        }
        return result;
    }

    private static bool IsIndexedMoveOccurrence(VfxOccurrence occurrence)
    {
        if (!occurrence.Active ||
            !string.Equals(occurrence.EventType, "MoveDecorations", StringComparison.Ordinal) ||
            occurrence.RepeatPlacementResolved == false)
        {
            return false;
        }
        return MoveDecorationsTargeting.GetTargetTags(occurrence.SourceEvent).Count > 0;
    }

    private static bool HasTags(string? tags) =>
        !string.IsNullOrEmpty(tags) && SplitTags(tags).Any();

    private static VfxOccurrence[] ResolveMoves(
        JsonNode? decorationTagNode,
        IReadOnlyDictionary<string, List<(int Order, VfxOccurrence Occurrence)>> movesByTag)
    {
        string? tags = ReadString(decorationTagNode);
        if (string.IsNullOrEmpty(tags))
            return [];

        var matches = new SortedDictionary<int, VfxOccurrence>();
        foreach (string tag in SplitTags(tags))
        {
            if (!movesByTag.TryGetValue(tag, out List<(int Order, VfxOccurrence Occurrence)>? entries))
                continue;
            foreach ((int order, VfxOccurrence occurrence) in entries)
                matches[order] = occurrence;
        }
        return matches.Values.ToArray();
    }

    private static IEnumerable<string> SplitTags(string tags) =>
        tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool HasMoveDecorations(LevelDocument level) =>
        level.ActionTypeCounts.TryGetValue("MoveDecorations", out int count) && count > 0;

    private static string? ReadString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
            return text;
        return node?.ToString();
    }

    private static bool ReadBool(JsonNode? node, bool defaultValue)
    {
        if (node is null)
            return defaultValue;
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool result))
                return result;
            if (value.TryGetValue(out string? text) && bool.TryParse(text, out result))
                return result;
        }
        return defaultValue;
    }

    private static float ReadFloat(JsonNode? node, float defaultValue)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out float single) && float.IsFinite(single))
                return single;
            if (value.TryGetValue(out double number) && double.IsFinite(number))
                return (float)number;
            if (float.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out single) &&
                float.IsFinite(single))
                return single;
        }
        return defaultValue;
    }

    private static int ReadInt(JsonNode? node, int defaultValue)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int result))
                return result;
            if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return result;
        }
        return defaultValue;
    }

    private static uint ReadColor(JsonNode? node)
    {
        string? text = ReadString(node)?.Trim().TrimStart('#');
        if (string.IsNullOrWhiteSpace(text))
            return 0x00FF_FFFFu;

        if (text.Length >= 6 &&
            uint.TryParse(text[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return rgb & 0x00FF_FFFFu;

        return 0x00FF_FFFFu;
    }
}
