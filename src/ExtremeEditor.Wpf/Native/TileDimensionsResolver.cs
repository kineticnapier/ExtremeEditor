using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct NativeTileDimensions(float Length, float Width);

internal readonly record struct TileDimensionsSourceEvent(
    int SourceIndex,
    int Floor,
    bool Active,
    double Length,
    double Width);

internal sealed record TileDimensionsSourceData(TileDimensionsSourceEvent[] Events);

internal static class TileDimensionsMetadataCache
{
    private static readonly ConditionalWeakTable<LevelDocument, TileDimensionsSourceData> Cache = new();

    internal static TileDimensionsSourceData Get(LevelDocument level) =>
        Cache.GetValue(level, static document => TileDimensionsSourceReader.LoadForDocument(document));
}

internal static class TileDimensionsSourceReader
{
    internal static readonly TileDimensionsSourceData DefaultData = new([]);

    internal static TileDimensionsSourceData LoadForDocument(LevelDocument level)
    {
        string path = level.SourcePath;
        if (string.IsNullOrWhiteSpace(path) || path == "<synthetic>" || !File.Exists(path))
            return DefaultData;

        try
        {
            return Load(path);
        }
        catch (JsonException)
        {
            string normalizedPath = LooseAdoFaiJson.CreateNormalizedTempCopy(path);
            try
            {
                return Load(normalizedPath);
            }
            finally
            {
                try { File.Delete(normalizedPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static TileDimensionsSourceData Load(string path)
    {
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
            return LoadUtf16(path);

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
                    throw new JsonException("Incomplete JSON token at end of TileDimensions pass.");
                return parser.Complete();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static TileDimensionsSourceData LoadUtf16(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (!document.RootElement.TryGetProperty("actions", out JsonElement actions) ||
            actions.ValueKind != JsonValueKind.Array)
            return DefaultData;

        var events = new List<TileDimensionsSourceEvent>();
        int sourceIndex = 0;
        foreach (JsonElement action in actions.EnumerateArray())
        {
            if (action.ValueKind == JsonValueKind.Object &&
                TryGetString(action, "eventType", out string? type) &&
                string.Equals(type, "TileDimensions", StringComparison.Ordinal) &&
                TryGetInt(action, "floor", out int floor))
            {
                bool active = !TryGetEnabled(action, "active", out bool enabled) || enabled;
                double length = TryGetDouble(action, "length", out double rawLength) ? rawLength : 100.0;
                double width = TryGetDouble(action, "width", out double rawWidth) ? rawWidth : 100.0;
                events.Add(new TileDimensionsSourceEvent(sourceIndex, floor, active, length, width));
            }
            sourceIndex++;
        }
        return new TileDimensionsSourceData(events.ToArray());
    }

    private sealed class Parser
    {
        private Mode _mode = Mode.Root;
        private Mode _resumeMode;
        private RootField _rootField;
        private Field _field;
        private ActionBuilder _action;
        private int _skipDepth;
        private int _nextSourceIndex;
        private readonly List<TileDimensionsSourceEvent> _events = [];

        internal TileDimensionsSourceData Complete() => new(_events.ToArray());

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
                case Mode.Root: AcceptRoot(ref reader); break;
                case Mode.Actions: AcceptActions(ref reader); break;
                case Mode.Action: AcceptAction(ref reader); break;
            }
        }

        private void AcceptRoot(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _rootField = reader.ValueTextEquals("actions"u8) ? RootField.Actions : RootField.Other;
                return;
            }

            RootField field = _rootField;
            _rootField = RootField.None;
            if (field == RootField.Actions && reader.TokenType == JsonTokenType.StartArray)
                _mode = Mode.Actions;
            else if (field != RootField.None && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                BeginSkip(Mode.Root);
        }

        private void AcceptActions(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                _mode = Mode.Root;
                return;
            }
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                _action = new ActionBuilder
                {
                    SourceIndex = _nextSourceIndex++,
                    Active = true,
                    Length = 100.0,
                    Width = 100.0
                };
                _field = Field.None;
                _mode = Mode.Action;
            }
            else if (reader.TokenType is JsonTokenType.StartArray)
            {
                _nextSourceIndex++;
                BeginSkip(Mode.Actions);
            }
        }

        private void AcceptAction(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _field == Field.None)
            {
                if (_action.HasFloor && string.Equals(_action.EventType, "TileDimensions", StringComparison.Ordinal))
                {
                    _events.Add(new TileDimensionsSourceEvent(
                        _action.SourceIndex,
                        _action.Floor,
                        _action.Active,
                        _action.Length,
                        _action.Width));
                }
                _mode = Mode.Actions;
                return;
            }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _field = MatchField(ref reader);
                return;
            }

            Field field = _field;
            _field = Field.None;
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(Mode.Action);
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
                    _action.Active = ReadEnabled(ref reader, true);
                    break;
                case Field.Length when TryReadDouble(ref reader, out double length):
                    _action.Length = length;
                    break;
                case Field.Width when TryReadDouble(ref reader, out double width):
                    _action.Width = width;
                    break;
            }
        }

        private void BeginSkip(Mode resume)
        {
            _resumeMode = resume;
            _skipDepth = 1;
            _mode = Mode.Skip;
        }

        private static Field MatchField(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("floor"u8)) return Field.Floor;
            if (reader.ValueTextEquals("eventType"u8)) return Field.EventType;
            if (reader.ValueTextEquals("active"u8)) return Field.Active;
            if (reader.ValueTextEquals("length"u8)) return Field.Length;
            if (reader.ValueTextEquals("width"u8)) return Field.Width;
            return Field.Other;
        }

        private enum Mode { Root, Actions, Action, Skip }
        private enum RootField { None, Actions, Other }
        private enum Field { None, Floor, EventType, Active, Length, Width, Other }

        private struct ActionBuilder
        {
            public int SourceIndex;
            public bool HasFloor;
            public int Floor;
            public string? EventType;
            public bool Active;
            public double Length;
            public double Width;
        }
    }

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return true;
    }

    private static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        value = default;
        if (!obj.TryGetProperty(name, out JsonElement element)) return false;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt32(out value);
        return element.ValueKind == JsonValueKind.String &&
               int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDouble(JsonElement obj, string name, out double value)
    {
        value = default;
        if (!obj.TryGetProperty(name, out JsonElement element)) return false;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDouble(out value);
        return element.ValueKind == JsonValueKind.String &&
               double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetEnabled(JsonElement obj, string name, out bool value)
    {
        value = default;
        if (!obj.TryGetProperty(name, out JsonElement element)) return false;
        if (element.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (element.ValueKind == JsonValueKind.False) { value = false; return true; }
        if (element.ValueKind != JsonValueKind.String) return false;
        string? text = element.GetString();
        if (bool.TryParse(text, out value)) return true;
        if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
        if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
        return false;
    }

    private static bool TryReadInt(ref Utf8JsonReader reader, out int value)
    {
        value = default;
        if (reader.TokenType == JsonTokenType.Number) return reader.TryGetInt32(out value);
        return reader.TokenType == JsonTokenType.String &&
               int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadDouble(ref Utf8JsonReader reader, out double value)
    {
        value = default;
        if (reader.TokenType == JsonTokenType.Number) return reader.TryGetDouble(out value);
        return reader.TokenType == JsonTokenType.String &&
               double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string? ReadString(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
        JsonTokenType.True => "True",
        JsonTokenType.False => "False",
        _ => null
    };

    private static bool ReadEnabled(ref Utf8JsonReader reader, bool fallback)
    {
        if (reader.TokenType == JsonTokenType.True) return true;
        if (reader.TokenType == JsonTokenType.False) return false;
        string? text = ReadString(ref reader);
        if (bool.TryParse(text, out bool parsed)) return parsed;
        if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase)) return false;
        return fallback;
    }
}

internal static class TileDimensionsResolver
{
    private readonly record struct LiveEvent(int SourceIndex, int Floor, bool Active, double Length, double Width);

    internal static NativeTileDimensions[] Resolve(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        int count = level.FloorCount;
        if (count == 0)
            return [];

        TileDimensionsSourceData source = TileDimensionsMetadataCache.Get(level);
        var bySource = source.Events.ToDictionary(static item => item.SourceIndex);
        var fallback = source.Events
            .GroupBy(static item => item.Floor)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<TileDimensionsSourceEvent>(group.OrderBy(static item => item.SourceIndex)));
        var used = new HashSet<int>();
        var live = new List<LiveEvent>();

        foreach (LevelAction action in level.ActionStore.Actions)
        {
            if (!string.Equals(action.EventType, "TileDimensions", StringComparison.Ordinal))
                continue;

            TileDimensionsSourceEvent? sourceEvent = null;
            if (action.SourceIndex >= 0 && bySource.TryGetValue(action.SourceIndex, out TileDimensionsSourceEvent exact))
            {
                sourceEvent = exact;
                used.Add(exact.SourceIndex);
            }
            else if (fallback.TryGetValue(action.Floor, out Queue<TileDimensionsSourceEvent>? queue))
            {
                while (queue.Count > 0 && used.Contains(queue.Peek().SourceIndex))
                    queue.Dequeue();
                if (queue.Count > 0)
                {
                    TileDimensionsSourceEvent matched = queue.Dequeue();
                    sourceEvent = matched;
                    used.Add(matched.SourceIndex);
                }
            }

            double length = GetDouble(action.PropertyOverrides, "length") ?? sourceEvent?.Length ?? 100.0;
            double width = GetDouble(action.PropertyOverrides, "width") ?? sourceEvent?.Width ?? 100.0;
            live.Add(new LiveEvent(action.SourceIndex, action.Floor, action.Active, length, width));
        }

        foreach (TileDimensionsSourceEvent item in source.Events)
        {
            if (!used.Contains(item.SourceIndex))
                live.Add(new LiveEvent(item.SourceIndex, item.Floor, item.Active, item.Length, item.Width));
        }

        live.Sort(static (a, b) =>
        {
            int floor = a.Floor.CompareTo(b.Floor);
            return floor != 0 ? floor : a.SourceIndex.CompareTo(b.SourceIndex);
        });

        var result = new NativeTileDimensions[count];
        float currentLength = 1f;
        float currentWidth = 1f;
        int cursor = 0;
        for (int floor = 0; floor < count; floor++)
        {
            while (cursor < live.Count && live[cursor].Floor < floor)
                cursor++;
            int eventCursor = cursor;
            while (eventCursor < live.Count && live[eventCursor].Floor == floor)
            {
                LiveEvent item = live[eventCursor++];
                if (!item.Active)
                    continue;
                if (double.IsFinite(item.Length)) currentLength = Math.Max(0f, (float)(item.Length / 100.0));
                if (double.IsFinite(item.Width)) currentWidth = Math.Max(0f, (float)(item.Width / 100.0));
            }
            cursor = eventCursor;
            result[floor] = new NativeTileDimensions(currentLength, currentWidth);
        }
        return result;
    }

    internal static void ApplyToMoveTimeline(LevelDocument level, NativeTrackTransformEvent[] events)
    {
        if (events.Length == 0)
            return;
        NativeTileDimensions[] dimensions = Resolve(level);
        foreach (ref NativeTrackTransformEvent item in events.AsSpan())
        {
            if ((uint)item.Floor >= (uint)dimensions.Length)
                continue;
            NativeTileDimensions dimension = dimensions[item.Floor];
            if ((item.Flags & NativeTrackTransformEvent.FlagScaleX) != 0u)
            {
                item.StartScaleX *= dimension.Length;
                item.TargetScaleX *= dimension.Length;
            }
            if ((item.Flags & NativeTrackTransformEvent.FlagScaleY) != 0u)
            {
                item.StartScaleY *= dimension.Width;
                item.TargetScaleY *= dimension.Width;
            }
        }
    }

    private static double? GetDouble(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out double number)) return number;
        if (value.TryGetValue(out float single)) return single;
        if (value.TryGetValue(out int integer)) return integer;
        return value.TryGetValue(out string? text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }
}
