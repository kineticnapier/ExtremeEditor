using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal sealed record TrackVisualSourceBundle(
    TrackColorSourceData Legacy,
    TrackVisualSourceData Visual);

internal sealed record TrackVisualSourceData(
    TrackVisualStyle InitialStyle,
    TrackVisualSourceEvent[] Events);

internal sealed record TrackVisualStyle(
    string ColorType,
    string PrimaryColor,
    string SecondaryColor,
    double AnimDuration,
    string PulseType,
    int PulseLength,
    string TrackStyle,
    double GlowIntensity,
    string TrackTexture,
    double TrackTextureScale,
    int StartFloor);

internal sealed record TrackVisualSourceEvent(
    int SourceIndex,
    int Floor,
    string EventType,
    bool Active,
    string? ColorType,
    string? PrimaryColor,
    string? SecondaryColor,
    double? AnimDuration,
    string? PulseType,
    int? PulseLength,
    string? TrackStyle,
    double? GlowIntensity,
    string? TrackTexture,
    double? TrackTextureScale,
    bool JustThisTile,
    int GapLength,
    TrackTileReference? StartTile,
    TrackTileReference? EndTile);

internal readonly record struct NativeTrackVisual(
    uint PrimaryColor,
    uint SecondaryColor,
    uint Flags,
    float AnimDuration,
    float GlowIntensity,
    int StartFloor,
    uint PulseLength);

internal static class TrackVisualMetadataCache
{
    private static readonly ConditionalWeakTable<LevelDocument, TrackVisualSourceData> Cache = new();

    internal static void Attach(LevelDocument level, TrackVisualSourceData data)
    {
        Cache.Remove(level);
        Cache.Add(level, data);
    }

    internal static TrackVisualSourceData Get(LevelDocument level) =>
        Cache.TryGetValue(level, out TrackVisualSourceData? data)
            ? data
            : TrackVisualSourceReader.DefaultBundle.Visual;
}

internal static class TrackVisualSourceReader
{
    internal static readonly TrackVisualSourceBundle DefaultBundle = BuildDefaultBundle();

    internal static TrackVisualSourceBundle Load(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return DefaultBundle;

        const int bufferSize = 1024 * 1024;
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = bufferSize,
            Options = FileOptions.SequentialScan
        };

        using var stream = new FileStream(path, options);
        Span<byte> prefix = stackalloc byte[3];
        int prefixLength = stream.Read(prefix);
        bool utf16Le = prefixLength >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE;
        bool utf16Be = prefixLength >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF;
        if (utf16Le || utf16Be)
            return DefaultBundle;

        bool utf8Bom = prefixLength >= 3 &&
                       prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
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
                cancellationToken.ThrowIfCancellationRequested();
                if (buffered == buffer.Length)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent(checked(buffer.Length * 2));
                    buffer.AsSpan(0, buffered).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int read = stream.Read(buffer, buffered, buffer.Length - buffered);
                buffered += read;
                bool finalBlock = read == 0;

                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), finalBlock, state);
                while (reader.Read())
                    parser.Accept(ref reader);

                int consumed = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                int remaining = buffered - consumed;
                if (remaining > 0 && consumed > 0)
                    buffer.AsSpan(consumed, remaining).CopyTo(buffer);
                buffered = remaining;

                if (!finalBlock)
                    continue;
                if (buffered != 0)
                    throw new JsonException("Incomplete JSON token at end of ADOFAI track visual pass.");
                return parser.Complete();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static TrackVisualSourceBundle BuildDefaultBundle()
    {
        var legacy = new TrackColorSourceData(
            new TrackColorStyle("Single", "debb7b", "ffffff", 10),
            []);
        var visual = new TrackVisualSourceData(
            new TrackVisualStyle(
                "Single",
                "debb7b",
                "ffffff",
                2.0,
                "None",
                10,
                "Standard",
                100.0,
                string.Empty,
                1.0,
                0),
            []);
        return new TrackVisualSourceBundle(legacy, visual);
    }

    private sealed class Parser
    {
        private ParserMode _mode = ParserMode.Root;
        private ParserMode _resumeMode;
        private RootField _rootField;
        private Field _field;
        private ActionBuilder _action;
        private Field _tileReferenceTarget;
        private int _tileReferenceIndex;
        private int _tileReferenceOffset;
        private string _tileReferenceMode = "ThisTile";
        private int _skipDepth;
        private int _nextSourceIndex;

        private string _colorType = "Single";
        private string _primaryColor = "debb7b";
        private string _secondaryColor = "ffffff";
        private double _animDuration = 2.0;
        private string _pulseType = "None";
        private int _pulseLength = 10;
        private string _trackStyle = "Standard";
        private double _glowIntensity = 100.0;
        private string _trackTexture = string.Empty;
        private double _trackTextureScale = 1.0;
        private readonly List<TrackVisualSourceEvent> _visualEvents = [];
        private readonly List<TrackColorSourceEvent> _legacyEvents = [];

        internal void Accept(ref Utf8JsonReader reader)
        {
            if (_mode == ParserMode.Skip)
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
                case ParserMode.Root:
                    AcceptRoot(ref reader);
                    break;
                case ParserMode.Settings:
                    AcceptSettings(ref reader);
                    break;
                case ParserMode.Actions:
                    AcceptActions(ref reader);
                    break;
                case ParserMode.Action:
                    AcceptAction(ref reader);
                    break;
                case ParserMode.TileReference:
                    AcceptTileReference(ref reader);
                    break;
            }
        }

        internal TrackVisualSourceBundle Complete()
        {
            var legacy = new TrackColorSourceData(
                new TrackColorStyle(_colorType, _primaryColor, _secondaryColor, _pulseLength),
                _legacyEvents.ToArray());
            var visual = new TrackVisualSourceData(
                new TrackVisualStyle(
                    _colorType,
                    _primaryColor,
                    _secondaryColor,
                    _animDuration,
                    _pulseType,
                    _pulseLength,
                    _trackStyle,
                    _glowIntensity,
                    _trackTexture,
                    _trackTextureScale,
                    0),
                _visualEvents.ToArray());
            return new TrackVisualSourceBundle(legacy, visual);
        }

        private void AcceptRoot(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _rootField = reader.ValueTextEquals("settings"u8)
                    ? RootField.Settings
                    : reader.ValueTextEquals("actions"u8)
                        ? RootField.Actions
                        : RootField.Other;
                return;
            }

            if (_rootField == RootField.None)
                return;

            RootField field = _rootField;
            _rootField = RootField.None;
            if (field == RootField.Settings && reader.TokenType == JsonTokenType.StartObject)
                _mode = ParserMode.Settings;
            else if (field == RootField.Actions && reader.TokenType == JsonTokenType.StartArray)
                _mode = ParserMode.Actions;
            else if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                BeginSkip(ParserMode.Root);
        }

        private void AcceptSettings(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _field == Field.None)
            {
                _mode = ParserMode.Root;
                return;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _field = MatchField(ref reader);
                return;
            }

            Field field = _field;
            _field = Field.None;
            if (field == Field.None)
                return;
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(ParserMode.Settings);
                return;
            }

            switch (field)
            {
                case Field.TrackColorType:
                    _colorType = ReadString(ref reader) ?? _colorType;
                    break;
                case Field.TrackColor:
                    _primaryColor = ReadString(ref reader) ?? _primaryColor;
                    break;
                case Field.SecondaryTrackColor:
                    _secondaryColor = ReadString(ref reader) ?? _secondaryColor;
                    break;
                case Field.TrackColorAnimDuration when TryReadDouble(ref reader, out double duration):
                    _animDuration = Math.Max(double.Epsilon, duration);
                    break;
                case Field.TrackColorPulse:
                    _pulseType = ReadString(ref reader) ?? _pulseType;
                    break;
                case Field.TrackPulseLength when TryReadInt(ref reader, out int pulseLength):
                    _pulseLength = Math.Max(1, pulseLength);
                    break;
                case Field.TrackStyle:
                    _trackStyle = ReadString(ref reader) ?? _trackStyle;
                    break;
                case Field.TrackGlowIntensity when TryReadDouble(ref reader, out double glow):
                    _glowIntensity = Math.Clamp(glow, 0.0, 100.0);
                    break;
                case Field.TrackTexture:
                    _trackTexture = ReadString(ref reader) ?? _trackTexture;
                    break;
                case Field.TrackTextureScale when TryReadDouble(ref reader, out double textureScale):
                    _trackTextureScale = Math.Max(double.Epsilon, textureScale);
                    break;
            }
        }

        private void AcceptActions(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                _mode = ParserMode.Root;
                return;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                _action = new ActionBuilder
                {
                    SourceIndex = _nextSourceIndex++,
                    Active = true,
                    GapLength = 0
                };
                _field = Field.None;
                _mode = ParserMode.Action;
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                BeginSkip(ParserMode.Actions);
            }
        }

        private void AcceptAction(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _field == Field.None)
            {
                FinalizeAction();
                _mode = ParserMode.Actions;
                return;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _field = MatchField(ref reader);
                return;
            }

            Field field = _field;
            _field = Field.None;
            if (field == Field.None)
                return;

            if (reader.TokenType == JsonTokenType.StartArray && field is Field.StartTile or Field.EndTile)
            {
                _tileReferenceTarget = field;
                _tileReferenceIndex = 0;
                _tileReferenceOffset = 0;
                _tileReferenceMode = "ThisTile";
                _mode = ParserMode.TileReference;
                return;
            }

            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(ParserMode.Action);
                return;
            }

            switch (field)
            {
                case Field.Floor when TryReadInt(ref reader, out int floor):
                    _action.Floor = floor;
                    _action.HasFloor = true;
                    break;
                case Field.EventType:
                    _action.EventType = ReadString(ref reader);
                    break;
                case Field.Active:
                    _action.Active = ReadBool(ref reader, true);
                    break;
                case Field.TrackColorType:
                    _action.ColorType = ReadString(ref reader);
                    break;
                case Field.TrackColor:
                    _action.PrimaryColor = ReadString(ref reader);
                    break;
                case Field.SecondaryTrackColor:
                    _action.SecondaryColor = ReadString(ref reader);
                    break;
                case Field.TrackColorAnimDuration when TryReadDouble(ref reader, out double duration):
                    _action.AnimDuration = duration;
                    break;
                case Field.TrackColorPulse:
                    _action.PulseType = ReadString(ref reader);
                    break;
                case Field.TrackPulseLength when TryReadInt(ref reader, out int pulseLength):
                    _action.PulseLength = pulseLength;
                    break;
                case Field.TrackStyle:
                    _action.TrackStyle = ReadString(ref reader);
                    break;
                case Field.TrackGlowIntensity when TryReadDouble(ref reader, out double glow):
                    _action.GlowIntensity = glow;
                    break;
                case Field.TrackTexture:
                    _action.TrackTexture = ReadString(ref reader);
                    break;
                case Field.TrackTextureScale when TryReadDouble(ref reader, out double textureScale):
                    _action.TrackTextureScale = textureScale;
                    break;
                case Field.JustThisTile:
                    _action.JustThisTile = ReadBool(ref reader, false);
                    break;
                case Field.GapLength when TryReadInt(ref reader, out int gap):
                    _action.GapLength = Math.Max(0, gap);
                    break;
            }
        }

        private void AcceptTileReference(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                var reference = new TrackTileReference(_tileReferenceOffset, _tileReferenceMode);
                if (_tileReferenceTarget == Field.StartTile)
                    _action.StartTile = reference;
                else if (_tileReferenceTarget == Field.EndTile)
                    _action.EndTile = reference;
                _mode = ParserMode.Action;
                return;
            }

            if (_tileReferenceIndex == 0 && TryReadInt(ref reader, out int offset))
                _tileReferenceOffset = offset;
            else if (_tileReferenceIndex == 1)
                _tileReferenceMode = ReadString(ref reader) ?? "ThisTile";
            _tileReferenceIndex++;
        }

        private void FinalizeAction()
        {
            if (!_action.HasFloor || _action.EventType is not ("ColorTrack" or "RecolorTrack"))
                return;

            _legacyEvents.Add(new TrackColorSourceEvent(
                _action.SourceIndex,
                _action.Floor,
                _action.EventType,
                _action.Active,
                _action.ColorType,
                _action.PrimaryColor,
                _action.SecondaryColor,
                _action.PulseLength,
                _action.JustThisTile,
                _action.GapLength,
                _action.StartTile,
                _action.EndTile));

            _visualEvents.Add(new TrackVisualSourceEvent(
                _action.SourceIndex,
                _action.Floor,
                _action.EventType,
                _action.Active,
                _action.ColorType,
                _action.PrimaryColor,
                _action.SecondaryColor,
                _action.AnimDuration,
                _action.PulseType,
                _action.PulseLength,
                _action.TrackStyle,
                _action.GlowIntensity,
                _action.TrackTexture,
                _action.TrackTextureScale,
                _action.JustThisTile,
                _action.GapLength,
                _action.StartTile,
                _action.EndTile));
        }

        private void BeginSkip(ParserMode resumeMode)
        {
            _resumeMode = resumeMode;
            _skipDepth = 1;
            _mode = ParserMode.Skip;
        }

        private static Field MatchField(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("floor"u8)) return Field.Floor;
            if (reader.ValueTextEquals("eventType"u8)) return Field.EventType;
            if (reader.ValueTextEquals("active"u8)) return Field.Active;
            if (reader.ValueTextEquals("trackColorType"u8)) return Field.TrackColorType;
            if (reader.ValueTextEquals("trackColor"u8)) return Field.TrackColor;
            if (reader.ValueTextEquals("secondaryTrackColor"u8)) return Field.SecondaryTrackColor;
            if (reader.ValueTextEquals("trackColorAnimDuration"u8)) return Field.TrackColorAnimDuration;
            if (reader.ValueTextEquals("trackColorPulse"u8)) return Field.TrackColorPulse;
            if (reader.ValueTextEquals("trackPulseLength"u8)) return Field.TrackPulseLength;
            if (reader.ValueTextEquals("trackStyle"u8)) return Field.TrackStyle;
            if (reader.ValueTextEquals("trackGlowIntensity"u8)) return Field.TrackGlowIntensity;
            if (reader.ValueTextEquals("trackTexture"u8)) return Field.TrackTexture;
            if (reader.ValueTextEquals("trackTextureScale"u8)) return Field.TrackTextureScale;
            if (reader.ValueTextEquals("justThisTile"u8)) return Field.JustThisTile;
            if (reader.ValueTextEquals("gapLength"u8)) return Field.GapLength;
            if (reader.ValueTextEquals("startTile"u8)) return Field.StartTile;
            if (reader.ValueTextEquals("endTile"u8)) return Field.EndTile;
            return Field.Other;
        }

        private static bool TryReadDouble(ref Utf8JsonReader reader, out double value)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out value))
                return true;
            if (reader.TokenType == JsonTokenType.String)
                return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            value = 0.0;
            return false;
        }

        private static bool TryReadInt(ref Utf8JsonReader reader, out int value)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out value))
                return true;
            if (reader.TokenType == JsonTokenType.String)
                return int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            value = 0;
            return false;
        }

        private static bool ReadBool(ref Utf8JsonReader reader, bool fallback)
        {
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.False) return false;
            string? text = ReadString(ref reader);
            return bool.TryParse(text, out bool parsed) ? parsed : fallback;
        }

        private static string? ReadString(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "True",
            JsonTokenType.False => "False",
            _ => null
        };

        private enum ParserMode { Root, Settings, Actions, Action, TileReference, Skip }
        private enum RootField { None, Settings, Actions, Other }
        private enum Field
        {
            None,
            Floor,
            EventType,
            Active,
            TrackColorType,
            TrackColor,
            SecondaryTrackColor,
            TrackColorAnimDuration,
            TrackColorPulse,
            TrackPulseLength,
            TrackStyle,
            TrackGlowIntensity,
            TrackTexture,
            TrackTextureScale,
            JustThisTile,
            GapLength,
            StartTile,
            EndTile,
            Other
        }

        private struct ActionBuilder
        {
            public int SourceIndex;
            public bool HasFloor;
            public int Floor;
            public string? EventType;
            public bool Active;
            public string? ColorType;
            public string? PrimaryColor;
            public string? SecondaryColor;
            public double? AnimDuration;
            public string? PulseType;
            public int? PulseLength;
            public string? TrackStyle;
            public double? GlowIntensity;
            public string? TrackTexture;
            public double? TrackTextureScale;
            public bool JustThisTile;
            public int GapLength;
            public TrackTileReference? StartTile;
            public TrackTileReference? EndTile;
        }
    }
}

internal static class TrackVisualResolver
{
    internal const uint FlagEnabled = 0x8000_0000u;
    internal const uint FlagUseTexture = 0x0000_0100u;
    internal const uint FlagCustomTexture = 0x0000_0200u;

    internal static NativeTrackVisual[] Resolve(LevelDocument level)
    {
        int floorCount = level.FloorCount;
        var output = new NativeTrackVisual[floorCount];
        if (floorCount == 0)
            return output;

        TrackVisualSourceData source = TrackVisualMetadataCache.Get(level);
        List<TrackVisualSourceEvent> live = BuildLiveEvents(level, source.Events);
        var styles = new TrackVisualStyle[floorCount];
        TrackVisualStyle persistent = source.InitialStyle;

        var colorEvents = live
            .Where(static item => item.Active && item.EventType == "ColorTrack")
            .GroupBy(static item => item.Floor)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static item => item.SourceIndex).ToArray());

        for (int floor = 0; floor < floorCount; floor++)
        {
            TrackVisualStyle floorStyle = persistent;
            if (colorEvents.TryGetValue(floor, out TrackVisualSourceEvent[]? events))
            {
                foreach (TrackVisualSourceEvent item in events)
                {
                    floorStyle = ApplyStyle(floorStyle, item, floor);
                    if (!item.JustThisTile)
                        persistent = floorStyle;
                }
            }
            styles[floor] = floorStyle;
        }

        foreach (TrackVisualSourceEvent item in live.Where(static item => item.Active && item.EventType == "RecolorTrack"))
        {
            int start = ResolveReference(item.StartTile, item.Floor, floorCount);
            int end = ResolveReference(item.EndTile, item.Floor, floorCount);
            if (end < start)
                (start, end) = (end, start);
            start = Math.Clamp(start, 0, floorCount - 1);
            end = Math.Clamp(end, 0, floorCount - 1);
            int gap = Math.Max(0, item.GapLength);
            int skipped = 0;
            for (int floor = start; floor <= end; floor++)
            {
                if (floor == start || skipped == gap)
                {
                    styles[floor] = ApplyStyle(styles[floor], item, item.Floor);
                    skipped = 0;
                }
                else
                {
                    skipped++;
                }
            }
        }

        for (int floor = 0; floor < floorCount; floor++)
            output[floor] = Pack(styles[floor]);
        return output;
    }

    private static TrackVisualStyle ApplyStyle(
        TrackVisualStyle fallback,
        TrackVisualSourceEvent item,
        int startFloor) => new(
            item.ColorType ?? fallback.ColorType,
            item.PrimaryColor ?? fallback.PrimaryColor,
            item.SecondaryColor ?? fallback.SecondaryColor,
            Math.Max(double.Epsilon, item.AnimDuration ?? fallback.AnimDuration),
            item.PulseType ?? fallback.PulseType,
            Math.Max(1, item.PulseLength ?? fallback.PulseLength),
            item.TrackStyle ?? fallback.TrackStyle,
            Math.Clamp(item.GlowIntensity ?? fallback.GlowIntensity, 0.0, 100.0),
            item.TrackTexture ?? fallback.TrackTexture,
            Math.Max(double.Epsilon, item.TrackTextureScale ?? fallback.TrackTextureScale),
            startFloor);

    private static NativeTrackVisual Pack(TrackVisualStyle style)
    {
        uint flags = FlagEnabled |
                     ParseColorType(style.ColorType) |
                     (ParseStyle(style.TrackStyle) << 3) |
                     (ParsePulse(style.PulseType) << 6);
        bool standardStyle = string.Equals(style.TrackStyle, "Standard", StringComparison.OrdinalIgnoreCase);
        if (standardStyle)
            flags |= FlagUseTexture;
        if (!string.IsNullOrWhiteSpace(style.TrackTexture))
            flags |= FlagCustomTexture;

        return new NativeTrackVisual(
            ParseColor(style.PrimaryColor, 0xFF7BBBDEu),
            ParseColor(style.SecondaryColor, 0xFFFFFFFFu),
            flags,
            (float)Math.Clamp(style.AnimDuration, 0.000001, 1000.0),
            (float)(Math.Clamp(style.GlowIntensity, 0.0, 100.0) * 0.01),
            style.StartFloor,
            checked((uint)Math.Clamp(style.PulseLength, 1, 1_000_000)));
    }

    private static uint ParseColorType(string? value) => value?.ToLowerInvariant() switch
    {
        "stripes" => 1u,
        "glow" => 2u,
        "blink" => 3u,
        "switch" => 4u,
        "rainbow" => 5u,
        "volume" => 6u,
        _ => 0u
    };

    private static uint ParseStyle(string? value) => value?.ToLowerInvariant() switch
    {
        "neon" => 1u,
        "neonlight" => 2u,
        "basic" => 3u,
        "minimal" => 4u,
        "gems" => 5u,
        _ => 0u
    };

    private static uint ParsePulse(string? value) => value?.ToLowerInvariant() switch
    {
        "forward" => 1u,
        "backward" => 2u,
        _ => 0u
    };

    private static uint ParseColor(string? text, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        string value = text.Trim().TrimStart('#');
        if (value.Length < 6 ||
            !byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return fallback;
        byte a = 255;
        if (value.Length >= 8)
            byte.TryParse(value.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a);
        return r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);
    }

    private static int ResolveReference(TrackTileReference? reference, int eventFloor, int floorCount)
    {
        if (reference is null)
            return eventFloor;
        return reference.Mode switch
        {
            "Start" => reference.Offset,
            "End" => floorCount - 1 + reference.Offset,
            _ => eventFloor + reference.Offset
        };
    }

    private static List<TrackVisualSourceEvent> BuildLiveEvents(
        LevelDocument level,
        IReadOnlyList<TrackVisualSourceEvent> sourceEvents)
    {
        var bySource = sourceEvents.ToDictionary(static item => item.SourceIndex);
        var fallback = sourceEvents
            .GroupBy(static item => (item.Floor, item.EventType))
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<TrackVisualSourceEvent>(group.OrderBy(static item => item.SourceIndex)));
        var usedSources = new HashSet<int>();
        var result = new List<TrackVisualSourceEvent>();

        foreach (LevelAction action in level.ActionStore.Actions)
        {
            if (action.EventType is not ("ColorTrack" or "RecolorTrack"))
                continue;

            TrackVisualSourceEvent? source = null;
            if (action.SourceIndex >= 0 && bySource.TryGetValue(action.SourceIndex, out TrackVisualSourceEvent? indexed))
            {
                source = indexed;
                usedSources.Add(indexed.SourceIndex);
            }
            else if (fallback.TryGetValue((action.Floor, action.EventType), out Queue<TrackVisualSourceEvent>? queue))
            {
                while (queue.Count > 0 && usedSources.Contains(queue.Peek().SourceIndex))
                    queue.Dequeue();
                if (queue.Count > 0)
                {
                    source = queue.Dequeue();
                    usedSources.Add(source.SourceIndex);
                }
            }

            TrackVisualSourceEvent live = source ?? new TrackVisualSourceEvent(
                action.SourceIndex,
                action.Floor,
                action.EventType,
                action.Active,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                0,
                null,
                null);

            live = live with
            {
                SourceIndex = action.SourceIndex,
                Floor = action.Floor,
                Active = action.Active
            };
            if (action.PropertyOverrides is JsonObject overrides)
                live = Overlay(live, overrides, action);
            result.Add(live);
        }

        result.Sort(static (a, b) =>
        {
            int floor = a.Floor.CompareTo(b.Floor);
            return floor != 0 ? floor : a.SourceIndex.CompareTo(b.SourceIndex);
        });
        return result;
    }

    private static TrackVisualSourceEvent Overlay(
        TrackVisualSourceEvent source,
        JsonObject obj,
        LevelAction action) => source with
        {
            Floor = action.Floor,
            Active = action.Active,
            ColorType = GetString(obj, "trackColorType") ?? source.ColorType,
            PrimaryColor = GetString(obj, "trackColor") ?? source.PrimaryColor,
            SecondaryColor = GetString(obj, "secondaryTrackColor") ?? source.SecondaryColor,
            AnimDuration = GetDouble(obj, "trackColorAnimDuration") ?? source.AnimDuration,
            PulseType = GetString(obj, "trackColorPulse") ?? source.PulseType,
            PulseLength = GetInt(obj, "trackPulseLength") ?? source.PulseLength,
            TrackStyle = GetString(obj, "trackStyle") ?? source.TrackStyle,
            GlowIntensity = GetDouble(obj, "trackGlowIntensity") ?? source.GlowIntensity,
            TrackTexture = GetString(obj, "trackTexture") ?? source.TrackTexture,
            TrackTextureScale = GetDouble(obj, "trackTextureScale") ?? source.TrackTextureScale,
            JustThisTile = GetBool(obj, "justThisTile") ?? source.JustThisTile,
            GapLength = Math.Max(0, GetInt(obj, "gapLength") ?? source.GapLength),
            StartTile = GetReference(obj["startTile"]) ?? source.StartTile,
            EndTile = GetReference(obj["endTile"]) ?? source.EndTile
        };

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue(out string? result) ? result : null;

    private static int? GetInt(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out int result))
            return result;
        return value.TryGetValue(out string? text) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
            ? result
            : null;
    }

    private static double? GetDouble(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out double result))
            return result;
        return value.TryGetValue(out string? text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            ? result
            : null;
    }

    private static bool? GetBool(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue(out bool result) ? result : null;

    private static TrackTileReference? GetReference(JsonNode? node)
    {
        if (node is not JsonArray { Count: >= 2 } pair)
            return null;
        int? offset = pair[0] is JsonValue first && first.TryGetValue(out int parsed) ? parsed : null;
        string? mode = pair[1] is JsonValue second && second.TryGetValue(out string? text) ? text : null;
        return offset is int value ? new TrackTileReference(value, mode ?? "ThisTile") : null;
    }
}
