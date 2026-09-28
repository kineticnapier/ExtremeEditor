using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Tests;

internal static class VfxTimelineCoreRegression
{
    private const string MissingCoreMessage =
        "VFX timeline core / occurrence builder is not available.";

    public static void Run()
    {
        LevelDocument level = CreateFixture();
        LevelAction[] sourceEvents = level.ActionStore.Actions.ToArray();
        SourceSnapshot[] sourceBefore = sourceEvents.Select(SourceSnapshot.Create).ToArray();

        OccurrenceContract contract = OccurrenceContract.Discover();
        IReadOnlyList<object> first = contract.Build(level);
        IReadOnlyList<object> second = contract.Build(level);

        VerifyOrdinaryOccurrences(contract, first, sourceEvents);
        VerifyRepeatRelationships(contract, first, sourceEvents);
        VerifyDeterministicOrder(contract, first, second);
        VerifySourcesWereNotMutated(sourceEvents, sourceBefore);
    }

    private static LevelDocument CreateFixture()
    {
        LevelDocument level = LevelDocument.CreateSynthetic(8);
        LevelAction beatTarget = CreateVfx(
            floor: 1,
            eventType: "MoveDecorations",
            active: true,
            sourceIndex: 0,
            angleOffset: 12.5,
            duration: 2.25,
            ease: "InOutSine",
            eventTag: "beat-target");
        LevelAction inactiveTarget = CreateVfx(
            floor: 2,
            eventType: "MoveTrack",
            active: false,
            sourceIndex: 1,
            angleOffset: -7.5,
            duration: 1.5,
            ease: "OutQuad",
            eventTag: "inactive-target");
        LevelAction floorTarget = CreateVfx(
            floor: 3,
            eventType: "MoveDecorations",
            active: true,
            sourceIndex: 2,
            angleOffset: 30,
            duration: 0.75,
            ease: "Linear",
            eventTag: "floor-target");
        LevelAction beatRepeat = CreateRepeat(
            floor: 4,
            sourceIndex: 3,
            repeatType: "Beat",
            eventTag: "beat-target");
        LevelAction floorRepeat = CreateRepeat(
            floor: 5,
            sourceIndex: 4,
            repeatType: "Floor",
            eventTag: "floor-target");

        level.ReplaceActions([beatTarget, inactiveTarget, floorTarget, beatRepeat, floorRepeat]);
        return level;
    }

    private static LevelAction CreateVfx(
        int floor,
        string eventType,
        bool active,
        int sourceIndex,
        double angleOffset,
        double duration,
        string ease,
        string eventTag)
    {
        return new LevelAction(floor, eventType, active, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            AngleOffset = angleOffset,
            Duration = duration,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = eventType,
                ["active"] = active,
                ["angleOffset"] = angleOffset,
                ["duration"] = duration,
                ["ease"] = ease,
                ["eventTag"] = eventTag,
                ["futureData"] = new JsonObject
                {
                    ["nested"] = new JsonArray(1, 2, 3)
                }
            }
        };
    }

    private static LevelAction CreateRepeat(
        int floor,
        int sourceIndex,
        string repeatType,
        string eventTag)
    {
        return new LevelAction(floor, "RepeatEvents", true, null, null, null, null)
        {
            SourceIndex = sourceIndex,
            PropertyOverrides = new JsonObject
            {
                ["floor"] = floor,
                ["eventType"] = "RepeatEvents",
                ["active"] = true,
                ["repeatType"] = repeatType,
                ["eventTag"] = eventTag,
                ["repetitions"] = 2,
                ["interval"] = 1,
                ["floorCount"] = 2,
                ["executeOnCurrentFloor"] = true
            }
        };
    }

    private static void VerifyOrdinaryOccurrences(
        OccurrenceContract contract,
        IReadOnlyList<object> occurrences,
        IReadOnlyList<LevelAction> sourceEvents)
    {
        foreach (LevelAction source in sourceEvents.Where(action => action.EventType != "RepeatEvents"))
        {
            object occurrence = occurrences.FirstOrDefault(candidate =>
                    !contract.IsRepeatDerived(candidate) &&
                    contract.GetSourceIndex(candidate) == source.SourceIndex)
                ?? throw new InvalidOperationException(
                    $"VFX timeline omitted source event {source.SourceIndex} ({source.EventType}).");

            AssertEqual(source.Floor, contract.GetFloor(occurrence), "Floor");
            AssertEqual(source.SourceIndex, contract.GetSourceIndex(occurrence), "SourceIndex");
            AssertEqual(source.EventType, contract.GetEventType(occurrence), "EventType");
            AssertEqual(source.Active, contract.GetActive(occurrence), "Active");
            AssertEqual(source.AngleOffset, contract.GetAngleOffset(occurrence), "angleOffset/degreeOffset");
            AssertEqual(source.Duration, contract.GetDuration(occurrence), "duration");
            AssertEqual(ReadString(source, "ease"), contract.GetEase(occurrence), "ease");
            AssertEqual(ReadString(source, "eventTag"), contract.GetEventTag(occurrence), "eventTag");

            object original = contract.GetSourceEvent(occurrence);
            if (original is not LevelAction originalAction ||
                originalAction.SourceIndex != source.SourceIndex)
            {
                throw new InvalidOperationException(
                    "A VFX occurrence does not retain its original LevelEvent.");
            }
        }

        object inactive = occurrences.First(candidate =>
            !contract.IsRepeatDerived(candidate) && contract.GetSourceIndex(candidate) == 1);
        if (contract.GetActive(inactive))
        {
            throw new InvalidOperationException(
                "An active:false VFX event was not preserved as raw occurrence information.");
        }
    }

    private static void VerifyRepeatRelationships(
        OccurrenceContract contract,
        IReadOnlyList<object> occurrences,
        IReadOnlyList<LevelAction> sourceEvents)
    {
        VerifyRepeatRelationship(contract, occurrences, sourceEvents, repeatSourceIndex: 3, "Beat", 0);
        VerifyRepeatRelationship(contract, occurrences, sourceEvents, repeatSourceIndex: 4, "Floor", 2);
    }

    private static void VerifyRepeatRelationship(
        OccurrenceContract contract,
        IReadOnlyList<object> occurrences,
        IReadOnlyList<LevelAction> sourceEvents,
        int repeatSourceIndex,
        string expectedRepeatType,
        int targetSourceIndex)
    {
        LevelAction repeat = sourceEvents.Single(action => action.SourceIndex == repeatSourceIndex);
        AssertEqual(expectedRepeatType, ReadString(repeat, "repeatType"), "RepeatEvents.repeatType");

        object? derived = occurrences.FirstOrDefault(candidate =>
            contract.IsRepeatDerived(candidate) &&
            contract.GetSourceIndex(candidate) == targetSourceIndex &&
            contract.GetRepeatSourceIndex(candidate) == repeatSourceIndex);
        if (derived is null)
        {
            throw new InvalidOperationException(
                $"RepeatEvents repeatType {expectedRepeatType} is not associated with its eventTag target as a derived occurrence.");
        }

        AssertEqual(expectedRepeatType, contract.GetRepeatType(derived), "repeatType");
        AssertEqual(
            ReadString(sourceEvents.Single(action => action.SourceIndex == targetSourceIndex), "eventTag"),
            contract.GetEventTag(derived),
            "derived eventTag");
    }

    private static void VerifyDeterministicOrder(
        OccurrenceContract contract,
        IReadOnlyList<object> first,
        IReadOnlyList<object> second)
    {
        string[] firstOrder = first.Select(contract.GetDeterministicSignature).ToArray();
        string[] secondOrder = second.Select(contract.GetDeterministicSignature).ToArray();
        if (!firstOrder.SequenceEqual(secondOrder, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "VFX occurrence ordering is not deterministic for identical input.");
        }
    }

    private static void VerifySourcesWereNotMutated(
        IReadOnlyList<LevelAction> sourceEvents,
        IReadOnlyList<SourceSnapshot> before)
    {
        SourceSnapshot[] after = sourceEvents.Select(SourceSnapshot.Create).ToArray();
        if (!before.SequenceEqual(after))
        {
            throw new InvalidOperationException(
                "Repeat occurrence expansion mutated an original LevelEvent.");
        }
    }

    private static string? ReadString(LevelAction action, string propertyName) =>
        action.PropertyOverrides?[propertyName]?.GetValue<string>();

    private static void AssertEqual<T>(T expected, T actual, string field)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"VFX occurrence did not preserve {field}.");
    }

    private sealed record SourceSnapshot(
        int Floor,
        string EventType,
        bool Active,
        int SourceIndex,
        double? AngleOffset,
        double? Duration,
        string? Properties)
    {
        public static SourceSnapshot Create(LevelAction action) => new(
            action.Floor,
            action.EventType,
            action.Active,
            action.SourceIndex,
            action.AngleOffset,
            action.Duration,
            action.PropertyOverrides?.ToJsonString());
    }

    private sealed class OccurrenceContract
    {
        private static readonly string[] BuilderMethodNames =
            ["Build", "BuildOccurrences", "Resolve", "Create"];

        private readonly MethodInfo _builder;
        private readonly PropertyInfo? _occurrencesProperty;
        private readonly Type _occurrenceType;

        private OccurrenceContract(
            MethodInfo builder,
            PropertyInfo? occurrencesProperty,
            Type occurrenceType)
        {
            _builder = builder;
            _occurrencesProperty = occurrencesProperty;
            _occurrenceType = occurrenceType;
        }

        public static OccurrenceContract Discover()
        {
            Assembly[] assemblies = [typeof(LevelDocument).Assembly, typeof(Program).Assembly];
            foreach (Type type in assemblies.SelectMany(GetLoadableTypes))
            {
                if (!type.Name.Contains("Vfx", StringComparison.OrdinalIgnoreCase) ||
                    (!type.Name.Contains("Timeline", StringComparison.OrdinalIgnoreCase) &&
                     !type.Name.Contains("Occurrence", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!BuilderMethodNames.Contains(method.Name, StringComparer.Ordinal) ||
                        method.GetParameters() is not [ParameterInfo parameter] ||
                        parameter.ParameterType != typeof(LevelDocument))
                    {
                        continue;
                    }

                    if (TryDescribeResult(method.ReturnType, out PropertyInfo? property, out Type? itemType))
                        return new OccurrenceContract(method, property, itemType!);
                }
            }

            throw new InvalidOperationException(MissingCoreMessage);
        }

        public IReadOnlyList<object> Build(LevelDocument level)
        {
            object? result = _builder.Invoke(null, [level]);
            object? sequence = _occurrencesProperty is null
                ? result
                : _occurrencesProperty.GetValue(result);
            if (sequence is not IEnumerable enumerable)
                throw new InvalidOperationException("VFX timeline builder did not return an occurrence sequence.");

            var occurrences = new List<object>();
            foreach (object? item in enumerable)
            {
                if (item is null || !_occurrenceType.IsInstanceOfType(item))
                    throw new InvalidOperationException("VFX timeline returned an invalid occurrence.");
                occurrences.Add(item);
            }
            return occurrences;
        }

        public int GetFloor(object occurrence) => GetRequired<int>(occurrence, "Floor");
        public int GetSourceIndex(object occurrence) => GetRequired<int>(occurrence, "SourceIndex");
        public string GetEventType(object occurrence) => GetRequired<string>(occurrence, "EventType");
        public bool GetActive(object occurrence) => GetRequired<bool>(occurrence, "Active");
        public double? GetAngleOffset(object occurrence) =>
            GetOptionalDouble(occurrence, "AngleOffset", "DegreeOffset");
        public double? GetDuration(object occurrence) => GetOptionalDouble(occurrence, "Duration");
        public string? GetEase(object occurrence) => GetOptionalString(occurrence, "Ease");
        public string? GetEventTag(object occurrence) =>
            GetOptionalString(occurrence, "EventTag", "Tag");
        public bool IsRepeatDerived(object occurrence) =>
            GetRequired<bool>(occurrence, "IsRepeatDerived", "IsRepeatOccurrence", "RepeatDerived");
        public object GetSourceEvent(object occurrence) =>
            GetRequired<object>(occurrence, "SourceEvent", "SourceLevelEvent", "OriginalEvent", "LevelEvent");
        public int GetRepeatSourceIndex(object occurrence)
        {
            object? direct = GetOptionalValue(occurrence, "RepeatSourceIndex", "RepeatOriginSourceIndex");
            if (direct is int sourceIndex)
                return sourceIndex;

            object? origin = GetOptionalValue(
                occurrence,
                "RepeatSource",
                "RepeatEvent",
                "RepeatOrigin",
                "SourceRepeatEvent");
            if (origin is LevelAction action)
                return action.SourceIndex;
            if (origin is not null)
                return GetRequired<int>(origin, "SourceIndex");

            throw new InvalidOperationException(
                "A repeat-derived VFX occurrence cannot identify its RepeatEvents source.");
        }

        public string GetRepeatType(object occurrence)
        {
            string? direct = GetOptionalString(occurrence, "RepeatType");
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            object? origin = GetOptionalValue(
                occurrence,
                "RepeatSource",
                "RepeatEvent",
                "RepeatOrigin",
                "SourceRepeatEvent");
            if (origin is LevelAction action)
                return ReadString(action, "repeatType") ?? string.Empty;
            if (origin is not null)
                return GetOptionalString(origin, "RepeatType") ?? string.Empty;
            return string.Empty;
        }

        public string GetDeterministicSignature(object occurrence) => string.Join(
            "|",
            GetFloor(occurrence),
            GetSourceIndex(occurrence),
            GetEventType(occurrence),
            GetActive(occurrence),
            GetAngleOffset(occurrence),
            GetDuration(occurrence),
            GetEase(occurrence),
            GetEventTag(occurrence),
            IsRepeatDerived(occurrence),
            IsRepeatDerived(occurrence) ? GetRepeatSourceIndex(occurrence) : -1);

        private static bool TryDescribeResult(
            Type resultType,
            out PropertyInfo? occurrencesProperty,
            out Type? occurrenceType)
        {
            occurrencesProperty = null;
            if (TryGetEnumerableItemType(resultType, out occurrenceType))
                return true;

            occurrencesProperty = resultType.GetProperty(
                "Occurrences",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return occurrencesProperty is not null &&
                   TryGetEnumerableItemType(occurrencesProperty.PropertyType, out occurrenceType);
        }

        private static bool TryGetEnumerableItemType(Type type, out Type? itemType)
        {
            Type? enumerable = type.IsGenericType &&
                               type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type
                : type.GetInterfaces().FirstOrDefault(candidate =>
                    candidate.IsGenericType &&
                    candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            itemType = enumerable?.GetGenericArguments()[0];
            return itemType is not null;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.OfType<Type>();
            }
        }

        private static T GetRequired<T>(object source, params string[] names)
        {
            object? value = GetOptionalValue(source, names);
            if (value is T typed)
                return typed;
            throw new InvalidOperationException(
                $"VFX occurrence does not expose {string.Join("/", names)}.");
        }

        private static double? GetOptionalDouble(object source, params string[] names)
        {
            object? value = GetOptionalValue(source, names);
            return value switch
            {
                null => null,
                double number => number,
                float number => number,
                int number => number,
                _ => throw new InvalidOperationException(
                    $"VFX occurrence {string.Join("/", names)} is not numeric.")
            };
        }

        private static string? GetOptionalString(object source, params string[] names)
        {
            object? value = GetOptionalValue(source, names);
            return value?.ToString();
        }

        private static object? GetOptionalValue(object source, params string[] names)
        {
            Type type = source.GetType();
            foreach (string name in names)
            {
                PropertyInfo? property = type.GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property is not null)
                    return property.GetValue(source);

                FieldInfo? field = type.GetField(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field is not null)
                    return field.GetValue(source);
            }
            return null;
        }
    }
}
