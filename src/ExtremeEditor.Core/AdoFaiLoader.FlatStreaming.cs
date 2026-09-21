using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ExtremeEditor.Core;

public static partial class AdoFaiLoader
{
    public static async Task<LoadResult> LoadFlatAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        const int bufferSize = 1024 * 1024;
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = bufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };

        await using var stream = new FileStream(path, options);
        long fileBytes = stream.Length;

        var readWatch = Stopwatch.StartNew();
        byte[] prefix = new byte[3];
        int prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            int n = await stream.ReadAsync(
                    prefix.AsMemory(prefixLength, prefix.Length - prefixLength),
                    cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
                break;
            prefixLength += n;
        }
        readWatch.Stop();

        bool utf16Le = prefixLength >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE;
        bool utf16Be = prefixLength >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF;
        if (utf16Le || utf16Be)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return await Task.Run(() => Load(path), cancellationToken).ConfigureAwait(false);
        }

        bool utf8Bom = prefixLength >= 3 &&
                       prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
        stream.Position = utf8Bom ? 3 : 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            var parser = new FlatStreamingParser();
            var state = new JsonReaderState(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            int buffered = 0;
            TimeSpan readTime = readWatch.Elapsed;
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
                int n = await stream.ReadAsync(
                        buffer.AsMemory(buffered, buffer.Length - buffered),
                        cancellationToken)
                    .ConfigureAwait(false);
                phase.Stop();
                readTime += phase.Elapsed;
                buffered += n;
                bool finalBlock = n == 0;

                phase.Restart();
                FlatReaderPassResult pass = ProcessFlatJsonBuffer(
                    buffer.AsSpan(0, buffered),
                    finalBlock,
                    state,
                    parser);
                phase.Stop();
                parseTime += phase.Elapsed;
                state = pass.State;

                int remaining = buffered - pass.BytesConsumed;
                if (remaining > 0 && pass.BytesConsumed > 0)
                    buffer.AsSpan(pass.BytesConsumed, remaining).CopyTo(buffer);
                buffered = remaining;

                if (!finalBlock)
                    continue;

                if (buffered != 0)
                    throw new JsonException("Incomplete JSON token at end of ADOFAI file.");
                break;
            }

            var finalize = Stopwatch.StartNew();
            FlatParseResult parsed = parser.Complete();
            finalize.Stop();
            parseTime += finalize.Elapsed;

            var pathWatch = Stopwatch.StartNew();
            var positions = PathBuilder.BuildPositions(parsed.Angles);
            var bounds = PathBuilder.CalculateBounds(positions);
            pathWatch.Stop();

            LevelActionStore actionStore = parsed.ActionStore;
            var document = new LevelDocument
            {
                SourcePath = path,
                Angles = parsed.Angles,
                Positions = positions,
                ActionCount = parsed.ActionCount,
                ActionTypeCounts = parsed.ActionTypeCounts,
                ActionsByFloor = actionStore.DictionaryView,
                ActionStore = actionStore,
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

    private static FlatReaderPassResult ProcessFlatJsonBuffer(
        ReadOnlySpan<byte> bytes,
        bool finalBlock,
        JsonReaderState state,
        FlatStreamingParser parser)
    {
        var reader = new Utf8JsonReader(bytes, finalBlock, state);
        while (reader.Read())
            parser.Accept(ref reader);
        return new FlatReaderPassResult(checked((int)reader.BytesConsumed), reader.CurrentState);
    }

    private readonly record struct FlatReaderPassResult(int BytesConsumed, JsonReaderState State);

    private sealed class FlatStreamingParser
    {
        private readonly List<double> _angles = new(65_536);
        private readonly List<LevelAction> _actions = new(262_144);
        private readonly List<LevelAction> _speedActions = new(256);
        private readonly Dictionary<string, int> _actionTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);

        private FlatParserMode _mode = FlatParserMode.Root;
        private FlatParserMode _resumeMode;
        private FlatRootField _rootField;
        private FlatSettingField _settingField;
        private FlatActionField _actionField;
        private FlatActionBuilder _action;
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

        public void Accept(ref Utf8JsonReader reader)
        {
            if (_mode == FlatParserMode.Skip)
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
                case FlatParserMode.Root:
                    AcceptRoot(ref reader);
                    break;
                case FlatParserMode.Angles:
                    AcceptAngle(ref reader);
                    break;
                case FlatParserMode.Settings:
                    AcceptSetting(ref reader);
                    break;
                case FlatParserMode.Actions:
                    AcceptActions(ref reader);
                    break;
                case FlatParserMode.Action:
                    AcceptAction(ref reader);
                    break;
            }
        }

        public FlatParseResult Complete()
        {
            if (!_sawAngleData)
                throw new InvalidDataException(
                    "This prototype currently requires angleData. Legacy pathData is not implemented.");

            ComputeSpeedRatios();
            LevelActionStore store = LevelActionStore.Create(_actions);
            return new FlatParseResult(
                _angles.ToArray(),
                _actionCount,
                _actionTypes,
                store,
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
                _rootField = reader.ValueTextEquals("angleData"u8) ? FlatRootField.AngleData :
                    reader.ValueTextEquals("settings"u8) ? FlatRootField.Settings :
                    reader.ValueTextEquals("actions"u8) ? FlatRootField.Actions : FlatRootField.Unknown;
                return;
            }

            FlatRootField field = _rootField;
            _rootField = FlatRootField.None;
            if (field == FlatRootField.AngleData && reader.TokenType == JsonTokenType.StartArray)
            {
                _sawAngleData = true;
                _mode = FlatParserMode.Angles;
            }
            else if (field == FlatRootField.Settings && reader.TokenType == JsonTokenType.StartObject)
            {
                _mode = FlatParserMode.Settings;
            }
            else if (field == FlatRootField.Actions && reader.TokenType == JsonTokenType.StartArray)
            {
                _mode = FlatParserMode.Actions;
            }
            else if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(FlatParserMode.Root);
            }
        }

        private void AcceptAngle(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                _mode = FlatParserMode.Root;
                return;
            }

            if (!TryReadDouble(ref reader, out double value))
                throw new InvalidDataException(
                    $"Unsupported angleData value at index {_angles.Count}: {reader.TokenType}");
            _angles.Add(value);
        }

        private void AcceptSetting(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _settingField == FlatSettingField.None)
            {
                _mode = FlatParserMode.Root;
                return;
            }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _settingField = MatchSetting(ref reader);
                return;
            }

            FlatSettingField field = _settingField;
            _settingField = FlatSettingField.None;
            if (field == FlatSettingField.None)
                return;
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(FlatParserMode.Settings);
                return;
            }

            switch (field)
            {
                case FlatSettingField.Bpm when TryReadDouble(ref reader, out double bpm) && bpm > 0:
                    _initialBpm = bpm;
                    break;
                case FlatSettingField.SongFilename:
                    _songFilename = ReadString(ref reader);
                    break;
                case FlatSettingField.Offset when TryReadDouble(ref reader, out double offset):
                    _offsetMilliseconds = offset;
                    break;
                case FlatSettingField.SongOffset when TryReadDouble(ref reader, out double songOffset):
                    _songOffsetMilliseconds = songOffset;
                    break;
                case FlatSettingField.Pitch when TryReadDouble(ref reader, out double pitch):
                    _pitchPercent = pitch;
                    break;
                case FlatSettingField.CountdownTicks when TryReadInt(ref reader, out int ticks) && ticks >= 0:
                    _countdownTicks = ticks;
                    break;
                case FlatSettingField.SeparateCountdownTime:
                    _separateCountdownTime = ReadBool(ref reader, false);
                    break;
                case FlatSettingField.HitSound:
                    _defaultHitSound = ReadString(ref reader) ?? "Kick";
                    break;
                case FlatSettingField.HitSoundVolume when TryReadDouble(ref reader, out double volume):
                    _hitSoundVolumePercent = volume;
                    break;
            }
        }

        private void AcceptActions(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                _mode = FlatParserMode.Root;
                return;
            }

            _actionCount++;
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                _action = new FlatActionBuilder { Active = true };
                _actionField = FlatActionField.None;
                _mode = FlatParserMode.Action;
            }
            else if (reader.TokenType is JsonTokenType.StartArray)
            {
                BeginSkip(FlatParserMode.Actions);
            }
        }

        private void AcceptAction(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.EndObject && _actionField == FlatActionField.None)
            {
                FinalizeAction();
                _mode = FlatParserMode.Actions;
                return;
            }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _actionField = MatchAction(ref reader);
                return;
            }

            FlatActionField field = _actionField;
            _actionField = FlatActionField.None;
            if (field == FlatActionField.None)
                return;
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                BeginSkip(FlatParserMode.Action);
                return;
            }

            switch (field)
            {
                case FlatActionField.Floor when TryReadInt(ref reader, out int floor):
                    _action.Floor = floor;
                    _action.HasFloor = true;
                    break;
                case FlatActionField.EventType:
                    _action.EventType = ReadEventType(ref reader);
                    break;
                case FlatActionField.Active:
                    _action.Active = ReadBool(ref reader, true);
                    break;
                case FlatActionField.SpeedType:
                    _action.SpeedType = ReadPooledString(ref reader);
                    break;
                case FlatActionField.BeatsPerMinute when TryReadDouble(ref reader, out double bpm):
                    _action.BeatsPerMinute = bpm;
                    break;
                case FlatActionField.BpmMultiplier when TryReadDouble(ref reader, out double multiplier):
                    _action.BpmMultiplier = multiplier;
                    break;
                case FlatActionField.Icon:
                    _action.CustomIcon = ReadPooledString(ref reader);
                    break;
                case FlatActionField.HitSound:
                    _action.HitSound = ReadPooledString(ref reader);
                    break;
                case FlatActionField.HitSoundVolume when TryReadDouble(ref reader, out double hitVolume):
                    _action.HitSoundVolumePercent = hitVolume;
                    break;
                case FlatActionField.GameSound:
                    _action.GameSound = ReadPooledString(ref reader);
                    break;
                case FlatActionField.Planets:
                    _action.Planets = ReadPooledString(ref reader);
                    break;
                case FlatActionField.AngleOffset when TryReadDouble(ref reader, out double angleOffset):
                    _action.AngleOffset = angleOffset;
                    break;
                case FlatActionField.Duration when TryReadDouble(ref reader, out double duration):
                    _action.Duration = duration;
                    break;
            }
        }

        private void FinalizeAction()
        {
            string type = _action.EventType ?? "<unknown>";
            _actionTypes.TryGetValue(type, out int count);
            _actionTypes[type] = count + 1;
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
            _actions.Add(action);
            if (action.Active && action.Kind == LevelActionKind.SetSpeed)
                _speedActions.Add(action);
        }

        private void ComputeSpeedRatios()
        {
            double bpm = _initialBpm > 0 ? _initialBpm : 100.0;
            foreach (LevelAction action in _speedActions.OrderBy(static action => action.Floor))
            {
                if (string.Equals(action.SpeedType, "Multiplier", StringComparison.OrdinalIgnoreCase) &&
                    action.BpmMultiplier is double multiplier && multiplier > 0)
                {
                    action.SpeedRatio = multiplier;
                    bpm *= multiplier;
                }
                else if (action.BeatsPerMinute is double target && target > 0)
                {
                    action.SpeedRatio = bpm > 0 ? target / bpm : null;
                    bpm = target;
                }
            }
        }

        private void BeginSkip(FlatParserMode resumeMode)
        {
            _resumeMode = resumeMode;
            _skipDepth = 1;
            _mode = FlatParserMode.Skip;
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

        private string? ReadPooledString(ref Utf8JsonReader reader)
        {
            string? value = ReadString(ref reader);
            if (string.IsNullOrEmpty(value))
                return value;
            if (_stringPool.TryGetValue(value, out string? pooled))
                return pooled;
            _stringPool.Add(value, value);
            return value;
        }

        private static FlatSettingField MatchSetting(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("bpm"u8)) return FlatSettingField.Bpm;
            if (reader.ValueTextEquals("songFilename"u8)) return FlatSettingField.SongFilename;
            if (reader.ValueTextEquals("offset"u8)) return FlatSettingField.Offset;
            if (reader.ValueTextEquals("songOffset"u8)) return FlatSettingField.SongOffset;
            if (reader.ValueTextEquals("pitch"u8)) return FlatSettingField.Pitch;
            if (reader.ValueTextEquals("countdownTicks"u8)) return FlatSettingField.CountdownTicks;
            if (reader.ValueTextEquals("separateCountdownTime"u8)) return FlatSettingField.SeparateCountdownTime;
            if (reader.ValueTextEquals("hitsound"u8)) return FlatSettingField.HitSound;
            if (reader.ValueTextEquals("hitsoundVolume"u8)) return FlatSettingField.HitSoundVolume;
            return FlatSettingField.Unknown;
        }

        private static FlatActionField MatchAction(ref Utf8JsonReader reader)
        {
            if (reader.ValueTextEquals("floor"u8)) return FlatActionField.Floor;
            if (reader.ValueTextEquals("eventType"u8)) return FlatActionField.EventType;
            if (reader.ValueTextEquals("active"u8)) return FlatActionField.Active;
            if (reader.ValueTextEquals("speedType"u8)) return FlatActionField.SpeedType;
            if (reader.ValueTextEquals("beatsPerMinute"u8)) return FlatActionField.BeatsPerMinute;
            if (reader.ValueTextEquals("bpmMultiplier"u8)) return FlatActionField.BpmMultiplier;
            if (reader.ValueTextEquals("icon"u8)) return FlatActionField.Icon;
            if (reader.ValueTextEquals("hitsound"u8)) return FlatActionField.HitSound;
            if (reader.ValueTextEquals("hitsoundVolume"u8)) return FlatActionField.HitSoundVolume;
            if (reader.ValueTextEquals("gameSound"u8)) return FlatActionField.GameSound;
            if (reader.ValueTextEquals("planets"u8)) return FlatActionField.Planets;
            if (reader.ValueTextEquals("angleOffset"u8)) return FlatActionField.AngleOffset;
            if (reader.ValueTextEquals("duration"u8)) return FlatActionField.Duration;
            return FlatActionField.Unknown;
        }

        private static bool TryReadDouble(ref Utf8JsonReader reader, out double value)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out value))
                return true;
            if (reader.TokenType == JsonTokenType.String)
                return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            value = 0;
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

        private static bool ReadBool(ref Utf8JsonReader reader, bool defaultValue)
        {
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.False) return false;
            string? text = ReadString(ref reader);
            if (bool.TryParse(text, out bool parsed)) return parsed;
            if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }

        private static string? ReadString(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "True",
            JsonTokenType.False => "False",
            JsonTokenType.Null => null,
            _ => null
        };

        private enum FlatParserMode { Root, Angles, Settings, Actions, Action, Skip }
        private enum FlatRootField { None, Unknown, AngleData, Settings, Actions }
        private enum FlatSettingField
        {
            None, Unknown, Bpm, SongFilename, Offset, SongOffset, Pitch,
            CountdownTicks, SeparateCountdownTime, HitSound, HitSoundVolume
        }
        private enum FlatActionField
        {
            None, Unknown, Floor, EventType, Active, SpeedType, BeatsPerMinute,
            BpmMultiplier, Icon, HitSound, HitSoundVolume, GameSound, Planets,
            AngleOffset, Duration
        }

        private struct FlatActionBuilder
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

    private sealed record FlatParseResult(
        double[] Angles,
        int ActionCount,
        IReadOnlyDictionary<string, int> ActionTypeCounts,
        LevelActionStore ActionStore,
        double InitialBpm,
        string? SongFilename,
        double OffsetMilliseconds,
        double PitchPercent,
        int CountdownTicks,
        bool SeparateCountdownTime,
        string DefaultHitSound,
        double HitSoundVolumePercent);
}
