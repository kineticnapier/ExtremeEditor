using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal readonly record struct ScaleRadiusSourceEvent(
    int SourceIndex,
    int Floor,
    bool Active,
    double Scale,
    bool EditorOnly);

internal sealed record ScaleRadiusSourceData(ScaleRadiusSourceEvent[] Events);

internal static class ScaleRadiusMetadataCache
{
    private static readonly ConditionalWeakTable<LevelDocument, ScaleRadiusSourceData> Cache = new();

    internal static void Attach(LevelDocument level, ScaleRadiusSourceData data)
    {
        Cache.Remove(level);
        Cache.Add(level, data);
    }

    internal static ScaleRadiusSourceData Get(LevelDocument level) =>
        Cache.TryGetValue(level, out ScaleRadiusSourceData? data)
            ? data
            : ScaleRadiusSourceReader.DefaultData;
}

internal static class ScaleRadiusSourceReader
{
    internal static readonly ScaleRadiusSourceData DefaultData = new([]);

    internal static ScaleRadiusSourceData Load(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return DefaultData;

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
            return LoadUtf16(path, cancellationToken);

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
                    throw new JsonException("Incomplete JSON token at end of ScaleRadius pass.");
                return parser.Complete();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ScaleRadiusSourceData LoadUtf16(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (!document.RootElement.TryGetProperty("actions", out JsonElement actions) ||
            actions.ValueKind != JsonValueKind.Array)
            return DefaultData;

        var events = new List<ScaleRadiusSourceEvent>();
        int sourceIndex = 0;
        foreach (JsonElement action in actions.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action.ValueKind == JsonValueKind.Object &&
                TryGetString(action, "eventType", out string? eventType) &&
                string.Equals(eventType, "ScaleRadius", StringComparison.Ordinal) &&
                TryGetInt(action, "floor", out int floor))
            {
                bool active = !TryGetEnabled(action, "active", out bool activeValue) || activeValue;
                bool editorOnly = TryGetEnabled(action, "editorOnly", out bool editorOnlyValue) && editorOnlyValue;
                double scale = TryGetDouble(action, "scale", out double scaleValue) ? scaleValue : 100.0;
                events.Add(new ScaleRadiusSourceEvent(sourceIndex, floor, active, scale, editorOnly));
            }
            sourceIndex++;
        }
        return new ScaleRadiusSourceData(events.ToArray());
    }

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out JsonElement element))
            return false;
        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }
        return false;
    }

    private static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        value = default;
        if (!obj.TryGetProperty(name, out JsonElement element))
            return false;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value);
        return element.ValueKind == JsonValueKind.String &&
               int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDouble(JsonElement obj, string name, out double value)
    {
        value = default;
        if (!obj.TryGetProperty(name, out JsonElement element))
            return false;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDouble(out value);
        return element.ValueKind == JsonValueKind.String &&
               double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetEnabled(JsonElement obj, string name, out bool value)
    {
        value = default;
        if (!obj.TryGetProperty(name, out JsonElement element))
            return false;
        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        if (element.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }
        if (element.ValueKind != JsonValueKind.String)
            return false;
        string? text = element.GetString();
        if (bool.TryParse(text, out value))
            return true;
        if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        return false;
    }

    private sealed class Parser
    {
        private Mode _mode = Mode.Root;
        private Mode _resumeMode;
        private RootField _rootField;
        private Field _field;
        private ActionBuilder _action;
        private int _nextSourceIndex;
        private int _skipDepth;
        private readonly List<ScaleRadiusSourceEvent> _events = [];

        internal ScaleRadiusSourceData Complete() => new(_events.ToArray());

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
                    AcceptRoot(ref reader);
                    break;
                case Mode.Actions:
                    AcceptActions(ref reader);
                    break;
                case Mode.Action:
                    AcceptAction(ref reader);
                    break;
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
                    Scale = 100.0
                };
                _field = Field.None;
                _mode = Mode.Action;
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                _nextSourceIndex++;
                BeginSkip(Mode.Actions);
            }
        }

        private void AcceptAction(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _field == Field.None)
            {
                FinishAction();
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
            if (field == Field.None)
                return;
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
                case Field.Scale when TryReadDouble(ref reader, out double scale):
                    _action.Scale = scale;
                    break;
                case Field.EditorOnly:
                    _action.EditorOnly = ReadEnabled(ref reader, false);
                    break;
            }
        }

        private void FinishAction()
        {
            if (!_action.HasFloor || !string.Equals(_action.EventType, "ScaleRadius", StringComparison.Ordinal))
                return;
            _events.Add(new ScaleRadiusSourceEvent(
                _action.SourceIndex,
                _action.Floor,
                _action.Active,
                _action.Scale,
                _action.EditorOnly));
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
            if (reader.ValueTextEquals("scale"u8)) return Field.Scale;
            if (reader.ValueTextEquals("editorOnly"u8)) return Field.EditorOnly;
            return Field.Other;
        }

        private static bool TryReadInt(ref Utf8JsonReader reader, out int value)
        {
            value = default;
            if (reader.TokenType == JsonTokenType.Number)
                return reader.TryGetInt32(out value);
            return reader.TokenType == JsonTokenType.String &&
                   int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadDouble(ref Utf8JsonReader reader, out double value)
        {
            value = default;
            if (reader.TokenType == JsonTokenType.Number)
                return reader.TryGetDouble(out value);
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

        private enum Mode { Root, Actions, Action, Skip }
        private enum RootField { None, Actions, Other }
        private enum Field { None, Floor, EventType, Active, Scale, EditorOnly, Other }

        private struct ActionBuilder
        {
            public int SourceIndex;
            public bool HasFloor;
            public int Floor;
            public string? EventType;
            public bool Active;
            public double Scale;
            public bool EditorOnly;
        }
    }
}

internal static class ScaleRadiusResolver
{
    private readonly record struct LiveEvent(int Floor, bool Active, double Scale);

    internal static void ApplyGeometry(LevelDocument level)
    {
        ArgumentNullException.ThrowIfNull(level);
        float[] radiusScales = ResolveScales(level, ScaleRadiusMetadataCache.Get(level));
        level.Positions = PathBuilder.BuildPositions(level.Angles, radiusScales);
        level.Bounds = PathBuilder.CalculateBounds(level.Positions);
    }

    internal static float[] ResolveScales(LevelDocument level, ScaleRadiusSourceData source)
    {
        int floorCount = level.Angles.Length + 1;
        if (floorCount <= 0)
            return [];

        var bySource = source.Events.ToDictionary(static item => item.SourceIndex);
        var fallback = source.Events
            .GroupBy(static item => item.Floor)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<ScaleRadiusSourceEvent>(group.OrderBy(static item => item.SourceIndex)));
        var used = new HashSet<int>();
        var live = new List<LiveEvent>();

        foreach (LevelAction action in level.ActionStore.Actions)
        {
            if (!string.Equals(action.EventType, "ScaleRadius", StringComparison.Ordinal))
                continue;

            ScaleRadiusSourceEvent? sourceEvent = null;
            if (action.SourceIndex >= 0 && bySource.TryGetValue(action.SourceIndex, out ScaleRadiusSourceEvent exact))
            {
                sourceEvent = exact;
                used.Add(exact.SourceIndex);
            }
            else if (fallback.TryGetValue(action.Floor, out Queue<ScaleRadiusSourceEvent>? queue))
            {
                while (queue.Count > 0 && used.Contains(queue.Peek().SourceIndex))
                    queue.Dequeue();
                if (queue.Count > 0)
                {
                    ScaleRadiusSourceEvent matched = queue.Dequeue();
                    sourceEvent = matched;
                    used.Add(matched.SourceIndex);
                }
            }

            double scale = TryReadScale(action.PropertyOverrides) ?? sourceEvent?.Scale ?? 100.0;
            live.Add(new LiveEvent(action.Floor, action.Active, scale));
        }

        var byFloor = live
            .Where(static item => item.Floor >= 0)
            .GroupBy(static item => item.Floor)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        var result = new float[floorCount];
        float current = 1.0f;
        for (int floor = 0; floor < floorCount; floor++)
        {
            if (byFloor.TryGetValue(floor, out LiveEvent[]? events))
            {
                foreach (LiveEvent item in events)
                {
                    if (!item.Active || !double.IsFinite(item.Scale))
                        continue;
                    current = (float)(item.Scale / 100.0);
                }
            }
            result[floor] = current;
        }
        return result;
    }

    private static double? TryReadScale(JsonObject? properties)
    {
        if (properties?["scale"] is not JsonValue value)
            return null;
        if (value.TryGetValue(out double number))
            return number;
        if (value.TryGetValue(out float single))
            return single;
        if (value.TryGetValue(out int integer))
            return integer;
        return value.TryGetValue(out string? text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }
}
