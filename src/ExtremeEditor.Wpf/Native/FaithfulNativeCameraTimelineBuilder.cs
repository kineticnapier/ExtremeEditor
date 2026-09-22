using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct CameraEventPresence(bool RelativeToSpecified, bool PositionSpecified);

internal sealed class CameraEventPresenceData
{
    internal static readonly CameraEventPresenceData Empty = new(new Dictionary<int, CameraEventPresence>());

    internal CameraEventPresenceData(Dictionary<int, CameraEventPresence> bySourceIndex) =>
        BySourceIndex = bySourceIndex;

    internal IReadOnlyDictionary<int, CameraEventPresence> BySourceIndex { get; }
}

/// <summary>
/// A tiny streaming pass used only for property-presence information that is lost
/// by the generic camera metadata model. In ADOFAI, an omitted relativeTo does not
/// mean Player; it means "keep the previous camera movement type".
/// </summary>
internal static class CameraEventPresenceReader
{
    private static readonly ConditionalWeakTable<LevelDocument, CameraEventPresenceData> Cache = new();

    internal static CameraEventPresenceData Get(LevelDocument level)
    {
        if (Cache.TryGetValue(level, out CameraEventPresenceData? cached))
            return cached;

        CameraEventPresenceData result;
        try
        {
            result = Load(level.SourcePath);
        }
        catch (JsonException)
        {
            string normalized = ExtremeEditor.Wpf.LooseAdoFaiJson.CreateNormalizedTempCopy(level.SourcePath);
            try
            {
                result = Load(normalized);
            }
            finally
            {
                try { File.Delete(normalized); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException)
        {
            result = CameraEventPresenceData.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            result = CameraEventPresenceData.Empty;
        }

        Cache.Add(level, result);
        return result;
    }

    private static CameraEventPresenceData Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return CameraEventPresenceData.Empty;

        const int bufferSize = 1024 * 1024;
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = bufferSize,
            Options = FileOptions.SequentialScan
        });

        Span<byte> prefix = stackalloc byte[3];
        int prefixLength = stream.Read(prefix);
        bool utf16Le = prefixLength >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE;
        bool utf16Be = prefixLength >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF;
        if (utf16Le || utf16Be)
            return CameraEventPresenceData.Empty;

        bool utf8Bom = prefixLength >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
        stream.Position = utf8Bom ? 3 : 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            var parser = new Parser();
            var state = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            int buffered = 0;
            while (true)
            {
                if (buffered == buffer.Length)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent(checked(buffer.Length * 2));
                    buffer.AsSpan(0, buffered).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int read = stream.Read(buffer, buffered, buffer.Length - buffered);
                buffered += read;
                bool final = read == 0;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), final, state);
                while (reader.Read())
                    parser.Accept(ref reader);

                int consumed = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                int remaining = buffered - consumed;
                if (remaining > 0 && consumed > 0)
                    buffer.AsSpan(consumed, remaining).CopyTo(buffer);
                buffered = remaining;

                if (!final)
                    continue;
                if (buffered != 0)
                    throw new JsonException("Incomplete JSON token at end of camera-presence pass.");
                return parser.Complete();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class Parser
    {
        private enum Mode { Root, Actions, Action, Skip }
        private Mode _mode;
        private Mode _resumeMode;
        private bool _nextRootValueIsActions;
        private int _skipDepth;
        private int _nextSourceIndex;
        private int _sourceIndex;
        private bool _relativeTo;
        private bool _position;
        private readonly Dictionary<int, CameraEventPresence> _result = new();

        internal void Accept(ref Utf8JsonReader reader)
        {
            if (_mode == Mode.Skip)
            {
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    _skipDepth++;
                else if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
                    _skipDepth--;
                if (_skipDepth == 0)
                    _mode = _resumeMode;
                return;
            }

            switch (_mode)
            {
                case Mode.Root:
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        _nextRootValueIsActions = reader.ValueTextEquals("actions"u8);
                    }
                    else if (_nextRootValueIsActions && reader.TokenType == JsonTokenType.StartArray)
                    {
                        _nextRootValueIsActions = false;
                        _mode = Mode.Actions;
                    }
                    else if (_nextRootValueIsActions)
                    {
                        _nextRootValueIsActions = false;
                    }
                    break;

                case Mode.Actions:
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        _mode = Mode.Root;
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        _sourceIndex = _nextSourceIndex++;
                        _relativeTo = false;
                        _position = false;
                        _mode = Mode.Action;
                    }
                    else if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        BeginSkip(Mode.Actions);
                    }
                    break;

                case Mode.Action:
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        _result[_sourceIndex] = new CameraEventPresence(_relativeTo, _position);
                        _mode = Mode.Actions;
                    }
                    else if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("relativeTo"u8)) _relativeTo = true;
                        if (reader.ValueTextEquals("position"u8)) _position = true;
                    }
                    else if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    {
                        BeginSkip(Mode.Action);
                    }
                    break;
            }
        }

        internal CameraEventPresenceData Complete() => new(_result);

        private void BeginSkip(Mode resume)
        {
            _resumeMode = resume;
            _skipDepth = 1;
            _mode = Mode.Skip;
        }
    }
}

/// <summary>
/// MoveCamera builder matching ffxCameraPlus's per-property DOTween behaviour.
/// Replacing a camera tween completes the previous tween first (DOKill(true));
/// relativeTo is stateful; LastPosition works on the camera rig rather than on
/// an already player-offset world position.
/// </summary>
internal static class FaithfulNativeCameraTimelineBuilder
{
    private const double InitialEventTime = -1.0e100;

    private readonly record struct PendingEvent(
        LiveEvent Live,
        double StartTime,
        double DurationSeconds);

    private readonly record struct LiveEvent(CameraSourceEvent Source, CameraEventPresence Presence);

    private sealed class ChannelState(float initial)
    {
        internal float Current = initial;
        internal float Target = initial;
        internal bool TweenActive;

        internal void KillComplete()
        {
            if (TweenActive)
                Current = Target;
            TweenActive = false;
        }

        internal void Start(float target, double duration)
        {
            Target = target;
            if (duration <= 1e-9)
            {
                Current = target;
                TweenActive = false;
            }
            else
            {
                TweenActive = true;
            }
        }
    }

    internal static NativeCameraEvent[] Build(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        if (level.FloorCount == 0 || timingMap.Floors.Count == 0)
            return [];

        CameraSourceData metadata = CameraMetadataCache.Get(level);
        CameraEventPresenceData presence = CameraEventPresenceReader.Get(level);
        List<LiveEvent> live = BuildLiveEvents(level, metadata.Events, presence);
        var pending = new List<PendingEvent>(live.Count);

        foreach (LiveEvent liveEvent in live)
        {
            CameraSourceEvent item = liveEvent.Source;
            if (!item.Active || (uint)item.Floor >= (uint)timingMap.Floors.Count)
                continue;

            FloorTiming timing = timingMap.Floors[item.Floor];
            double bpm = timing.Bpm > 0.0 ? timing.Bpm : Math.Max(0.000001, level.InitialBpm);
            double beatSeconds = 60.0 / bpm;
            pending.Add(new PendingEvent(
                liveEvent,
                timing.EntryTime + item.AngleOffset / 180.0 * beatSeconds,
                Math.Max(0.0, item.Duration) * beatSeconds));
        }

        pending.Sort(static (a, b) =>
        {
            int byTime = a.StartTime.CompareTo(b.StartTime);
            return byTime != 0 ? byTime : a.Live.Source.SourceIndex.CompareTo(b.Live.Source.SourceIndex);
        });

        StaticTrackTransform[] staticTransforms = TrackTransformResolver.ResolveStatic(level);
        NativeTrackTransformEvent[] moveTimeline = TrackTransformResolver.BuildMoveTimeline(
            level,
            timingMap,
            staticTransforms);

        string movement = NormalizeMovement(metadata.InitialRelativeTo);
        bool followMode = movement == "Player";
        int lastTileCamFloor = -1;
        var lastEventRelativePosition = new System.Numerics.Vector2(0f, 0f);

        float initialX = (float)(metadata.InitialPosition.X ?? 0.0);
        float initialY = (float)(metadata.InitialPosition.Y ?? 0.0);
        if (movement == "Tile")
        {
            var floorPos = TrackTransformPositionSampler.Evaluate(
                0,
                double.NegativeInfinity,
                staticTransforms,
                moveTimeline);
            initialX += floorPos.X;
            initialY += floorPos.Y;
            lastEventRelativePosition = floorPos;
            lastTileCamFloor = 0;
        }
        else if (movement == "Global")
        {
            lastEventRelativePosition = System.Numerics.Vector2.Zero;
        }

        var xState = new ChannelState(initialX);
        var yState = new ChannelState(initialY);
        var rotationState = new ChannelState((float)(metadata.InitialRotation * Math.PI / 180.0));
        var zoomState = new ChannelState((float)Math.Clamp(metadata.InitialZoom * 0.01, 0.01, 100.0));

        uint initialFlags = NativeCameraEvent.FlagApplyMask;
        if (followMode)
            initialFlags |= NativeCameraEvent.FlagTargetPlayerX | NativeCameraEvent.FlagTargetPlayerY;

        var output = new List<NativeCameraEvent>(pending.Count + 1)
        {
            new NativeCameraEvent
            {
                StartTime = InitialEventTime,
                DurationSeconds = 0.0,
                StartX = initialX,
                StartY = initialY,
                TargetX = initialX,
                TargetY = initialY,
                StartRotation = rotationState.Current,
                TargetRotation = rotationState.Current,
                StartZoom = zoomState.Current,
                TargetZoom = zoomState.Current,
                Flags = initialFlags,
                Ease = NativeCameraEvent.EaseLinear
            }
        };

        foreach (PendingEvent pendingEvent in pending)
        {
            CameraSourceEvent item = pendingEvent.Live.Source;
            CameraEventPresence fields = pendingEvent.Live.Presence;
            var pose = timingMap.GetPose(level, pendingEvent.StartTime);
            float playerX = pose.StationaryPlanet.X;
            float playerY = pose.StationaryPlanet.Y;

            bool positionUsed = fields.PositionSpecified;
            bool movementTypeUsed = fields.RelativeToSpecified;
            string requestedMovement = NormalizeMovement(item.RelativeTo);
            bool xSpecified = item.Position.X is double;
            bool ySpecified = item.Position.Y is double;

            if (movementTypeUsed &&
                requestedMovement is not "Global" and not "LastPosition" and not "LastPositionNoRotation" &&
                positionUsed && (!xSpecified || !ySpecified) &&
                requestedMovement == movement &&
                (requestedMovement != "Tile" || item.Floor == lastTileCamFloor))
            {
                movementTypeUsed = false;
            }

            string effectiveMovement = movementTypeUsed ? requestedMovement : movement;
            bool isLastPosition = effectiveMovement is "LastPosition" or "LastPositionNoRotation";
            bool rotationUsed = item.Rotation is double;
            bool zoomUsed = item.Zoom is double;

            if (positionUsed || movementTypeUsed)
            {
                if (xSpecified || movementTypeUsed) xState.KillComplete();
                if (ySpecified || movementTypeUsed) yState.KillComplete();
            }
            if (rotationUsed || (movementTypeUsed && isLastPosition))
                rotationState.KillComplete();
            if (zoomUsed)
                zoomState.KillComplete();

            float vectorX = xSpecified ? (float)item.Position.X!.Value : float.NaN;
            float vectorY = ySpecified ? (float)item.Position.Y!.Value : float.NaN;
            if (movementTypeUsed)
            {
                if (float.IsNaN(vectorX)) vectorX = 0f;
                if (float.IsNaN(vectorY)) vectorY = 0f;
            }

            float beforeX = xState.Current;
            float beforeY = yState.Current;
            float vector2X = positionUsed ? vectorX : lastEventRelativePosition.X - beforeX;
            float vector2Y = positionUsed ? vectorY : lastEventRelativePosition.Y - beforeY;
            float finalX = beforeX;
            float finalY = beforeY;
            float rotationOffset = 0f;

            switch (effectiveMovement)
            {
                case "Player":
                    if (!followMode)
                    {
                        xState.Current = beforeX - playerX;
                        yState.Current = beforeY - playerY;
                        beforeX = xState.Current;
                        beforeY = yState.Current;
                        followMode = true;
                    }
                    finalX = vector2X;
                    finalY = vector2Y;
                    break;

                case "Tile":
                    if (followMode)
                    {
                        xState.Current = beforeX + playerX;
                        yState.Current = beforeY + playerY;
                        beforeX = xState.Current;
                        beforeY = yState.Current;
                        followMode = false;
                    }
                    {
                        int floor = Math.Clamp(item.Floor, 0, level.Positions.Length - 1);
                        var floorPos = TrackTransformPositionSampler.Evaluate(
                            floor,
                            pendingEvent.StartTime,
                            staticTransforms,
                            moveTimeline);
                        lastEventRelativePosition = floorPos;
                        lastTileCamFloor = floor;
                        finalX = vector2X + floorPos.X;
                        finalY = vector2Y + floorPos.Y;
                    }
                    break;

                case "Global":
                    if (followMode)
                    {
                        xState.Current = beforeX + playerX;
                        yState.Current = beforeY + playerY;
                        beforeX = xState.Current;
                        beforeY = yState.Current;
                        followMode = false;
                    }
                    lastEventRelativePosition = System.Numerics.Vector2.Zero;
                    finalX = vector2X;
                    finalY = vector2Y;
                    break;

                case "LastPosition":
                case "LastPositionNoRotation":
                    if (effectiveMovement == "LastPosition")
                        rotationOffset = rotationState.Current;
                    finalX = positionUsed ? beforeX + vectorX : beforeX;
                    finalY = positionUsed ? beforeY + vectorY : beforeY;
                    break;
            }

            if (movementTypeUsed)
                movement = requestedMovement;

            uint flags = 0u;
            var next = new NativeCameraEvent
            {
                StartTime = pendingEvent.StartTime,
                DurationSeconds = pendingEvent.DurationSeconds,
                Ease = ParseEase(item.Ease)
            };

            if ((positionUsed || movementTypeUsed) && !float.IsNaN(finalX))
            {
                next.StartX = followMode ? xState.Current + playerX : xState.Current;
                next.TargetX = finalX;
                flags |= NativeCameraEvent.FlagApplyX;
                if (followMode) flags |= NativeCameraEvent.FlagTargetPlayerX;
                xState.Start(finalX, pendingEvent.DurationSeconds);
            }
            if ((positionUsed || movementTypeUsed) && !float.IsNaN(finalY))
            {
                next.StartY = followMode ? yState.Current + playerY : yState.Current;
                next.TargetY = finalY;
                flags |= NativeCameraEvent.FlagApplyY;
                if (followMode) flags |= NativeCameraEvent.FlagTargetPlayerY;
                yState.Start(finalY, pendingEvent.DurationSeconds);
            }

            if (rotationUsed || (movementTypeUsed && isLastPosition))
            {
                float target = (float)((item.Rotation ?? 0.0) * Math.PI / 180.0) + rotationOffset;
                next.StartRotation = rotationState.Current;
                next.TargetRotation = target;
                flags |= NativeCameraEvent.FlagApplyRotation;
                rotationState.Start(target, pendingEvent.DurationSeconds);
            }

            if (zoomUsed)
            {
                float target = (float)Math.Clamp(item.Zoom!.Value * 0.01, 0.01, 100.0);
                next.StartZoom = zoomState.Current;
                next.TargetZoom = target;
                flags |= NativeCameraEvent.FlagApplyZoom;
                zoomState.Start(target, pendingEvent.DurationSeconds);
            }

            next.Flags = flags;
            if ((flags & NativeCameraEvent.FlagApplyMask) != 0u)
                output.Add(next);
        }

        return output.ToArray();
    }

    private static List<LiveEvent> BuildLiveEvents(
        LevelDocument level,
        IReadOnlyList<CameraSourceEvent> sourceEvents,
        CameraEventPresenceData presenceData)
    {
        var bySource = sourceEvents.ToDictionary(static item => item.SourceIndex);
        var fallback = sourceEvents
            .GroupBy(static item => item.Floor)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<CameraSourceEvent>(group.OrderBy(static item => item.SourceIndex)));
        var usedSources = new HashSet<int>();
        var result = new List<LiveEvent>();

        foreach (LevelAction action in level.ActionStore.Actions)
        {
            if (!string.Equals(action.EventType, "MoveCamera", StringComparison.Ordinal))
                continue;

            CameraSourceEvent? source = null;
            if (action.SourceIndex >= 0 && bySource.TryGetValue(action.SourceIndex, out CameraSourceEvent? indexed))
            {
                source = indexed;
                usedSources.Add(indexed.SourceIndex);
            }
            else if (fallback.TryGetValue(action.Floor, out Queue<CameraSourceEvent>? queue))
            {
                while (queue.Count > 0 && usedSources.Contains(queue.Peek().SourceIndex))
                    queue.Dequeue();
                if (queue.Count > 0)
                {
                    source = queue.Dequeue();
                    usedSources.Add(source.SourceIndex);
                }
            }

            int sourceIndex = source?.SourceIndex ?? action.SourceIndex;
            CameraEventPresence presence = sourceIndex >= 0 &&
                presenceData.BySourceIndex.TryGetValue(sourceIndex, out CameraEventPresence found)
                    ? found
                    : default;

            CameraSourceEvent live = source ?? new CameraSourceEvent(
                action.SourceIndex,
                action.Floor,
                action.Active,
                action.Duration ?? 1.0,
                "Player",
                new CameraSourcePosition(null, null),
                null,
                null,
                action.AngleOffset ?? 0.0,
                "Linear");

            live = live with
            {
                SourceIndex = sourceIndex,
                Floor = action.Floor,
                Active = action.Active,
                Duration = action.Duration ?? live.Duration,
                AngleOffset = action.AngleOffset ?? live.AngleOffset
            };

            if (action.PropertyOverrides is JsonObject obj)
            {
                if (obj.ContainsKey("relativeTo")) presence = presence with { RelativeToSpecified = true };
                if (obj.ContainsKey("position")) presence = presence with { PositionSpecified = true };
                live = Overlay(live, obj, action);
            }

            result.Add(new LiveEvent(live, presence));
        }

        return result;
    }

    private static CameraSourceEvent Overlay(CameraSourceEvent source, JsonObject obj, LevelAction action)
    {
        CameraSourcePosition position = source.Position;
        if (obj.ContainsKey("position") && obj["position"] is JsonArray pair)
        {
            double scale = PathBuilder.DefaultLongTileSize;
            position = new CameraSourcePosition(
                pair.Count > 0 && GetDouble(pair[0]) is double x ? x * scale : null,
                pair.Count > 1 && GetDouble(pair[1]) is double y ? y * scale : null);
        }

        return source with
        {
            Floor = action.Floor,
            Active = action.Active,
            Duration = GetDouble(obj["duration"]) ?? action.Duration ?? source.Duration,
            RelativeTo = GetString(obj["relativeTo"]) ?? source.RelativeTo,
            Position = position,
            Rotation = obj.ContainsKey("rotation") ? GetDouble(obj["rotation"]) : source.Rotation,
            Zoom = obj.ContainsKey("zoom") ? GetDouble(obj["zoom"]) : source.Zoom,
            AngleOffset = GetDouble(obj["angleOffset"]) ?? action.AngleOffset ?? source.AngleOffset,
            Ease = GetString(obj["ease"]) ?? source.Ease
        };
    }

    private static string NormalizeMovement(string? value) => value switch
    {
        "Tile" => "Tile",
        "Global" => "Global",
        "LastPosition" => "LastPosition",
        "LastPositionNoRotation" => "LastPositionNoRotation",
        _ => "Player"
    };

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? result) ? result : null;

    private static double? GetDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out double number))
            return number;
        return value.TryGetValue(out string? text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static uint ParseEase(string? ease) => ease switch
    {
        "InSine" => NativeCameraEvent.EaseInSine,
        "OutSine" => NativeCameraEvent.EaseOutSine,
        "InOutSine" => NativeCameraEvent.EaseInOutSine,
        "InQuad" => NativeCameraEvent.EaseInQuad,
        "OutQuad" => NativeCameraEvent.EaseOutQuad,
        "InOutQuad" => NativeCameraEvent.EaseInOutQuad,
        "InCubic" => NativeCameraEvent.EaseInCubic,
        "OutCubic" => NativeCameraEvent.EaseOutCubic,
        "InOutCubic" => NativeCameraEvent.EaseInOutCubic,
        "InQuart" => NativeCameraEvent.EaseInQuart,
        "OutQuart" => NativeCameraEvent.EaseOutQuart,
        "InOutQuart" => NativeCameraEvent.EaseInOutQuart,
        "InQuint" => NativeCameraEvent.EaseInQuint,
        "OutQuint" => NativeCameraEvent.EaseOutQuint,
        "InOutQuint" => NativeCameraEvent.EaseInOutQuint,
        "InExpo" => NativeCameraEvent.EaseInExpo,
        "OutExpo" => NativeCameraEvent.EaseOutExpo,
        "InOutExpo" => NativeCameraEvent.EaseInOutExpo,
        "InCirc" => NativeCameraEvent.EaseInCirc,
        "OutCirc" => NativeCameraEvent.EaseOutCirc,
        "InOutCirc" => NativeCameraEvent.EaseInOutCirc,
        "InBack" => NativeCameraEvent.EaseInBack,
        "OutBack" => NativeCameraEvent.EaseOutBack,
        "InOutBack" => NativeCameraEvent.EaseInOutBack,
        "InElastic" => NativeCameraEvent.EaseInElastic,
        "OutElastic" => NativeCameraEvent.EaseOutElastic,
        "InOutElastic" => NativeCameraEvent.EaseInOutElastic,
        "InBounce" => NativeCameraEvent.EaseInBounce,
        "OutBounce" => NativeCameraEvent.EaseOutBounce,
        "InOutBounce" => NativeCameraEvent.EaseInOutBounce,
        _ => NativeCameraEvent.EaseLinear
    };
}
