using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ExtremeEditor.Core;

public static partial class AdoFaiLoader
{
    private const int StreamingBufferSize = 1024 * 1024;

    private static readonly JsonReaderOptions StreamingJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Streams UTF-8 ADOFAI JSON directly into the editor model. The file is read
    /// asynchronously in bounded chunks and no JsonDocument for the whole file is
    /// created. UTF-16 files keep the legacy compatibility path.
    /// </summary>
    public static async Task<LoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = StreamingBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };

        await using var stream = new FileStream(path, options);
        long fileBytes = stream.Length;

        var readWatch = Stopwatch.StartNew();
        byte[] prefix = new byte[3];
        int prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            int read = await stream.ReadAsync(
                    prefix.AsMemory(prefixLength, prefix.Length - prefixLength),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            prefixLength += read;
        }
        readWatch.Stop();

        bool utf16Le = prefixLength >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE;
        bool utf16Be = prefixLength >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF;
        if (utf16Le || utf16Be)
        {
            // UTF-16 ADOFAI exists in the wild but is rare. Keep exact legacy
            // normalization semantics instead of complicating the hot UTF-8 path.
            await stream.DisposeAsync().ConfigureAwait(false);
            return await Task.Run(() => Load(path), cancellationToken).ConfigureAwait(false);
        }

        bool utf8Bom = prefixLength >= 3 &&
                       prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
        stream.Position = utf8Bom ? 3 : 0;

        return await LoadUtf8StreamAsync(
                path,
                stream,
                fileBytes,
                readWatch.Elapsed,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<LoadResult> LoadUtf8StreamAsync(
        string path,
        FileStream stream,
        long fileBytes,
        TimeSpan initialReadTime,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamingBufferSize);
        try
        {
            int buffered = 0;
            var parser = new StreamingAdoFaiParser();
            var readerState = new JsonReaderState(StreamingJsonOptions);
            TimeSpan readTime = initialReadTime;
            TimeSpan parseTime = TimeSpan.Zero;

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

                var phase = Stopwatch.StartNew();
                int read = await stream.ReadAsync(
                        buffer.AsMemory(buffered, buffer.Length - buffered),
                        cancellationToken)
                    .ConfigureAwait(false);
                phase.Stop();
                readTime += phase.Elapsed;
                buffered += read;
                bool finalBlock = read == 0;

                phase.Restart();
                ReaderPassResult pass = ProcessJsonBuffer(
                    buffer.AsSpan(0, buffered),
                    finalBlock,
                    readerState,
                    parser);
                phase.Stop();
                parseTime += phase.Elapsed;
                readerState = pass.State;

                int remaining = buffered - pass.BytesConsumed;
                if (remaining > 0 && pass.BytesConsumed > 0)
                    buffer.AsSpan(pass.BytesConsumed, remaining).CopyTo(buffer);
                buffered = remaining;

                if (finalBlock)
                {
                    if (buffered != 0)
                        throw new JsonException("Incomplete JSON token at end of ADOFAI file.");
                    break;
                }
            }

            var finalizeWatch = Stopwatch.StartNew();
            StreamingParseResult parsed = parser.Complete();
            finalizeWatch.Stop();
            parseTime += finalizeWatch.Elapsed;

            var pathWatch = Stopwatch.StartNew();
            var positions = PathBuilder.BuildPositions(parsed.Angles);
            var bounds = PathBuilder.CalculateBounds(positions);
            pathWatch.Stop();

            var document = new LevelDocument
            {
                SourcePath = path,
                Angles = parsed.Angles,
                Positions = positions,
                ActionCount = parsed.ActionCount,
                ActionTypeCounts = parsed.ActionTypeCounts,
                ActionsByFloor = parsed.ActionsByFloor,
                InitialBpm = parsed.InitialBpm,
                SongFilename = parsed.SongFilename,
                OffsetMilliseconds = parsed.OffsetMilliseconds,
                PitchPercent = parsed.PitchPercent,
                CountdownTicks = parsed.CountdownTicks,
                SeparateCountdownTime = parsed.SeparateCountdownTime,
                DefaultHitSound = parsed.DefaultHitSound,
                HitSoundVolumePercent = parsed.HitSoundVolumePercent,
                Bounds = bounds
            };

            return new LoadResult(
                document,
                new LoadMetrics(readTime, parseTime, pathWatch.Elapsed, fileBytes));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ReaderPassResult ProcessJsonBuffer(
        ReadOnlySpan<byte> bytes,
        bool finalBlock,
        JsonReaderState state,
        StreamingAdoFaiParser parser)
    {
        var reader = new Utf8JsonReader(bytes, finalBlock, state);
        while (reader.Read())
            parser.Accept(ref reader);

        return new ReaderPassResult(checked((int)reader.BytesConsumed), reader.CurrentState);
    }

    private readonly record struct ReaderPassResult(int BytesConsumed, JsonReaderState State);

    private sealed class StreamingAdoFaiParser
    {
        private readonly List<double> _angles = new(65_536);
        private readonly Dictionary<string, int> _actionTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<int, LevelAction[]> _actionsByFloor = new();
        private readonly List<LevelAction> _speedActions = new();
        private readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);

        private ParserMode _mode = ParserMode.Root;
        private ParserMode _resumeMode;
        private RootField _rootField;
        private SettingField _settingField;
        private ActionField _actionField;
        private ActionBuilder _action;
        private int _skipDepth;
        private bool _sawAngleData;
        private int _actionCount;

        private double _initialBpm = 100.0;
        private string? _songFilename;
        private double? _offsetMilliseconds;
        private double? _songOffsetMilliseconds;
        private double _pitchPercent = 100.0;
        private int _countdownTicks = 4;
        private bool _separateCountdownTime;
        private string _defaultHitSound = "Kick";
        private double _hitSoundVolumePercent = 100.0;

        internal void Accept(ref Utf8JsonReader reader)
        {
            if (_mode == ParserMode.Skip)
            {
                AcceptSkipped(ref reader);
                return;
            }

            switch (_mode)
            {
                case ParserMode.Root:
                    AcceptRoot(ref reader);
                    break;
                case ParserMode.Angles:
                    AcceptAngle(ref reader);
                    break;
                case ParserMode.Settings:
                    AcceptSetting(ref reader);
                    break;
                case ParserMode.Actions:
                    AcceptActions(ref reader);
                    break;
                case ParserMode.Action:
                    AcceptAction(ref reader);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected parser mode: {_mode}");
            }
        }

        internal StreamingParseResult Complete()
        {
            if (!_sawAngleData)
            {
                throw new InvalidDataException(
                    "This prototype currently requires angleData. Legacy pathData is not implemented.");
            }

            ComputeStreamingSpeedRatios();
            return new StreamingParseResult(
                _angles.ToArray(),
                _actionCount,
                _actionTypes,
                _actionsByFloor,
                _initialBpm,
                _songFilename,
                _offsetMilliseconds ?? _songOffsetMilliseconds ?? 0.0,
                _pitchPercent,
                _countdownTicks,
                _separateCountdownTime,
                _defaultHitSound,
                _hitSoundVolumePercent);
        }

        private void AcceptRoot(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _rootField = MatchRootField(ref reader);
                return;
            }

            if (_rootField == RootField.None)
                return;

            RootField field = _rootField;
            _rootField = RootField.None;
            if (field == RootField.AngleData && reader.TokenType == JsonTokenType.StartArray)
            {
                _sawAngleData = true;
                _mode = ParserMode.Angles;
                return;
            }

            if (field == RootField.Settings && reader.TokenType == JsonTokenType.StartObject)
            {
                _mode = ParserMode.Settings;
                return;
            }

            if (field == RootField.Actions && reader.TokenType == JsonTokenType.StartArray)
            {
                _mode = ParserMode.Actions;
                return;
            }

            SkipComplexIfNeeded(ref reader, ParserMode.Root);
        }

        private void AcceptAngle(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                _mode = ParserMode.Root;
                return;
            }

            if (!TryReadDoubleToken(ref reader, out double value))
            {
                throw new InvalidDataException(
                    $"Unsupported angleData value at index {_angles.Count}: {reader.TokenType}");
            }

            _angles.Add(value);
        }

        private void AcceptSetting(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _settingField == SettingField.None)
            {
                _mode = ParserMode.Root;
                return;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _settingField = MatchSettingField(ref reader);
                return;
            }

            SettingField field = _settingField;
            _settingField = SettingField.None;
            if (field == SettingField.None)
                return;

            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(ParserMode.Settings);
                return;
            }

            switch (field)
            {
                case SettingField.Bpm:
                    if (TryReadDoubleToken(ref reader, out double bpm) && bpm > 0)
                        _initialBpm = bpm;
                    break;
                case SettingField.SongFilename:
                    _songFilename = ReadLooseStringToken(ref reader);
                    break;
                case SettingField.Offset:
                    if (TryReadDoubleToken(ref reader, out double offset))
                        _offsetMilliseconds = offset;
                    break;
                case SettingField.SongOffset:
                    if (TryReadDoubleToken(ref reader, out double songOffset))
                        _songOffsetMilliseconds = songOffset;
                    break;
                case SettingField.Pitch:
                    if (TryReadDoubleToken(ref reader, out double pitch))
                        _pitchPercent = pitch;
                    break;
                case SettingField.CountdownTicks:
                    if (TryReadIntToken(ref reader, out int ticks) && ticks >= 0)
                        _countdownTicks = ticks;
                    break;
                case SettingField.SeparateCountdownTime:
                    _separateCountdownTime = ReadLooseBoolToken(ref reader, false);
                    break;
                case SettingField.HitSound:
                    _defaultHitSound = ReadLooseStringToken(ref reader) ?? "Kick";
                    break;
                case SettingField.HitSoundVolume:
                    if (TryReadDoubleToken(ref reader, out double volume))
                        _hitSoundVolumePercent = volume;
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

            _actionCount++;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                _action = new ActionBuilder { Active = true };
                _actionField = ActionField.None;
                _mode = ParserMode.Action;
                return;
            }

            if (reader.TokenType is JsonTokenType.StartArray)
                BeginSkip(ParserMode.Actions);
        }

        private void AcceptAction(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _actionField == ActionField.None)
            {
                FinalizeAction();
                _mode = ParserMode.Actions;
                return;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _actionField = MatchActionField(ref reader);
                return;
            }

            ActionField field = _actionField;
            _actionField = ActionField.None;
            if (field == ActionField.None)
                return;

            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(ParserMode.Action);
                return;
            }

            switch (field)
            {
                case ActionField.Floor:
                    if (TryReadIntToken(ref reader, out int floor))
                    {
                        _action.Floor = floor;
                        _action.HasFloor = true;
                    }
                    break;
                case ActionField.EventType:
                    _action.EventType = ReadEventType(ref reader);
                    break;
                case ActionField.Active:
                    _action.Active = ReadLooseBoolToken(ref reader, true);
                    break;
                case ActionField.SpeedType:
                    _action.SpeedType = ReadPooledString(ref reader);
                    break;
                case ActionField.BeatsPerMinute:
                    if (TryReadDoubleToken(ref reader, out double bpm))
                        _action.BeatsPerMinute = bpm;
                    break;
                case ActionField.BpmMultiplier:
                    if (TryReadDoubleToken(ref reader, out double multiplier))
                        _action.BpmMultiplier = multiplier;
                    break;
                case ActionField.Icon:
                    _action.CustomIcon = ReadPooledString(ref reader);
                    break;
                case ActionField.HitSound:
                    _action.HitSound = ReadPooledString(ref reader);
                    break;
                case ActionField.HitSoundVolume:
                    if (TryReadDoubleToken(ref reader, out double hitVolume))
                        _action.HitSoundVolumePercent = hitVolume;
                    break;
                case ActionField.GameSound:
                    _action.GameSound = ReadPooledString(ref reader);
                    break;
                case ActionField.Planets:
                    _action.Planets = ReadPooledString(ref reader);
                    break;
                case ActionField.AngleOffset:
                    if (TryReadDoubleToken(ref reader, out double angleOffset))
                        _action.AngleOffset = angleOffset;
                    break;
                case ActionField.Duration:
                    if (TryReadDoubleToken(ref reader, out double duration))
                        _action.Duration = duration;
                    break;
            }
        }

        private void FinalizeAction()
        {
            string type = _action.EventType ?? "<unknown>";
            _actionTypes.TryGetValue(type, out int typeCount);
            _actionTypes[type] = typeCount + 1;

            if (!_action.HasFloor)
                return;

            var action = new LevelAction(
                _action.Floor,
                type,
                _action.Active,
                _action.SpeedType,
                _action.BeatsPerMinute,
                _action.BpmMultiplier,
                _action.CustomIcon)
            {
                HitSound = _action.HitSound,
                HitSoundVolumePercent = _action.HitSoundVolumePercent,
                GameSound = _action.GameSound,
                Planets = _action.Planets,
                AngleOffset = _action.AngleOffset,
                Duration = _action.Duration
            };

            if (_actionsByFloor.TryGetValue(action.Floor, out LevelAction[]? existing))
            {
                var expanded = new LevelAction[existing.Length + 1];
                Array.Copy(existing, expanded, existing.Length);
                expanded[^1] = action;
                _actionsByFloor[action.Floor] = expanded;
            }
            else
            {
                _actionsByFloor.Add(action.Floor, [action]);
            }

            if (action.Active && string.Equals(type, "SetSpeed", StringComparison.Ordinal))
                _speedActions.Add(action);
        }

        private void ComputeStreamingSpeedRatios()
        {
            double bpm = _initialBpm > 0 ? _initialBpm : 100.0;
            foreach (LevelAction action in _speedActions.OrderBy(action => action.Floor))
            {
                if (string.Equals(action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
                    action.BpmMultiplier is double multiplier && multiplier > 0)
                {
                    action.SpeedRatio = multiplier;
                    bpm *= multiplier;
                }
                else if (action.BeatsPerMinute is double targetBpm && targetBpm > 0)
                {
                    action.SpeedRatio = bpm > 0 ? targetBpm / bpm : null;
                    bpm = targetBpm;
                }
            }
        }

        private void BeginSkip(ParserMode resumeMode)
        {
            _resumeMode = resumeMode;
            _skipDepth = 1;
            _mode = ParserMode.Skip;
        }

        private void SkipComplexIfNeeded(ref Utf8JsonReader reader, ParserMode resumeMode)
        {
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                BeginSkip(resumeMode);
        }

        private void AcceptSkipped(ref Utf8JsonReader reader)
        {
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                _skipDepth++;
            else if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
                _skipDepth--;

            if (_skipDepth == 0)
                _mode = _resumeMode;
        }

        private string? ReadPooledString(ref Utf8JsonReader reader)
        {
            string? value = ReadLooseStringToken(ref reader);
            if (value is null || value.Length == 0)
                return value;
            if (_stringPool.TryGetValue(value, out string? existing))
                return existing;
            _stringPool.Add(value, value);
            return value;
        }

        private string? ReadEventType(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                if (reader.ValueTextEquals("Twirl"u8)) return "Twirl";
                if (reader.ValueTextEquals("SetSpeed"u8)) return "SetSpeed";
                if (reader.ValueTextEquals("MultiPlanet"u8)) return "MultiPlanet";
                if (reader.ValueTextEquals("Pause"u8)) return "Pause";
                if (reader.ValueTextEquals("SetHitsound"u8)) return "SetHitsound";
                if (reader.ValueTextEquals("SetFloorIcon"u8)) return "SetFloorIcon";
                if (reader.ValueTextEquals("Checkpoint"u8)) return "Checkpoint";
            }
            return ReadPooledString(ref reader);
        }

        private static RootField MatchRootField(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("angleData"u8)) return RootField.AngleData;
            if (reader.ValueTextEquals("settings"u8)) return RootField.Settings;
            if (reader.ValueTextEquals("actions"u8)) return RootField.Actions;
            return RootField.Unknown;
        }

        private static SettingField MatchSettingField(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("bpm"u8)) return SettingField.Bpm;
            if (reader.ValueTextEquals("songFilename"u8)) return SettingField.SongFilename;
            if (reader.ValueTextEquals("offset"u8)) return SettingField.Offset;
            if (reader.ValueTextEquals("songOffset"u8)) return SettingField.SongOffset;
            if (reader.ValueTextEquals("pitch"u8)) return SettingField.Pitch;
            if (reader.ValueTextEquals("countdownTicks"u8)) return SettingField.CountdownTicks;
            if (reader.ValueTextEquals("separateCountdownTime"u8)) return SettingField.SeparateCountdownTime;
            if (reader.ValueTextEquals("hitsound"u8)) return SettingField.HitSound;
            if (reader.ValueTextEquals("hitsoundVolume"u8)) return SettingField.HitSoundVolume;
            return SettingField.Unknown;
        }

        private static ActionField MatchActionField(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("floor"u8)) return ActionField.Floor;
            if (reader.ValueTextEquals("eventType"u8)) return ActionField.EventType;
            if (reader.ValueTextEquals("active"u8)) return ActionField.Active;
            if (reader.ValueTextEquals("speedType"u8)) return ActionField.SpeedType;
            if (reader.ValueTextEquals("beatsPerMinute"u8)) return ActionField.BeatsPerMinute;
            if (reader.ValueTextEquals("bpmMultiplier"u8)) return ActionField.BpmMultiplier;
            if (reader.ValueTextEquals("icon"u8)) return ActionField.Icon;
            if (reader.ValueTextEquals("hitsound"u8)) return ActionField.HitSound;
            if (reader.ValueTextEquals("hitsoundVolume"u8)) return ActionField.HitSoundVolume;
            if (reader.ValueTextEquals("gameSound"u8)) return ActionField.GameSound;
            if (reader.ValueTextEquals("planets"u8)) return ActionField.Planets;
            if (reader.ValueTextEquals("angleOffset"u8)) return ActionField.AngleOffset;
            if (reader.ValueTextEquals("duration"u8)) return ActionField.Duration;
            return ActionField.Unknown;
        }

        private static bool TryReadDoubleToken(ref Utf8JsonReader reader, out double result)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out result))
                return true;
            if (reader.TokenType == JsonTokenType.String)
            {
                string? text = reader.GetString();
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            }
            result = 0;
            return false;
        }

        private static bool TryReadIntToken(ref Utf8JsonReader reader, out int result)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out result))
                return true;
            if (reader.TokenType == JsonTokenType.String)
            {
                string? text = reader.GetString();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            }
            result = 0;
            return false;
        }

        private static bool ReadLooseBoolToken(ref Utf8JsonReader reader, bool defaultValue)
        {
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.False) return false;
            string? text = ReadLooseStringToken(ref reader);
            if (bool.TryParse(text, out bool parsed)) return parsed;
            if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }

        private static string? ReadLooseStringToken(ref Utf8JsonReader reader)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
                JsonTokenType.True => "True",
                JsonTokenType.False => "False",
                JsonTokenType.Null => null,
                _ => null
            };
        }

        private enum ParserMode
        {
            Root,
            Angles,
            Settings,
            Actions,
            Action,
            Skip
        }

        private enum RootField
        {
            None,
            Unknown,
            AngleData,
            Settings,
            Actions
        }

        private enum SettingField
        {
            None,
            Unknown,
            Bpm,
            SongFilename,
            Offset,
            SongOffset,
            Pitch,
            CountdownTicks,
            SeparateCountdownTime,
            HitSound,
            HitSoundVolume
        }

        private enum ActionField
        {
            None,
            Unknown,
            Floor,
            EventType,
            Active,
            SpeedType,
            BeatsPerMinute,
            BpmMultiplier,
            Icon,
            HitSound,
            HitSoundVolume,
            GameSound,
            Planets,
            AngleOffset,
            Duration
        }

        private struct ActionBuilder
        {
            public bool HasFloor;
            public int Floor;
            public string? EventType;
            public bool Active;
            public string? SpeedType;
            public double? BeatsPerMinute;
            public double? BpmMultiplier;
            public string? CustomIcon;
            public string? HitSound;
            public double? HitSoundVolumePercent;
            public string? GameSound;
            public string? Planets;
            public double? AngleOffset;
            public double? Duration;
        }
    }

    private sealed record StreamingParseResult(
        double[] Angles,
        int ActionCount,
        IReadOnlyDictionary<string, int> ActionTypeCounts,
        IReadOnlyDictionary<int, LevelAction[]> ActionsByFloor,
        double InitialBpm,
        string? SongFilename,
        double OffsetMilliseconds,
        double PitchPercent,
        int CountdownTicks,
        bool SeparateCountdownTime,
        string DefaultHitSound,
        double HitSoundVolumePercent);
}
