using System.Reflection;
using ExtremeEditor.Wpf;

namespace ExtremeEditor.Wpf.Tests;

internal static class EventAvailabilityRegression
{
    private static readonly string[] AllAddEventTypes =
    [
        "SetSpeed", "Twirl", "Multitap", "Checkpoint", "SetHitsound", "PlaySound",
        "SetPlanetRotation", "KillPlayer", "Pause", "AutoPlayTiles", "ScalePlanets",
        "ColorTrack", "AnimateTrack", "RecolorTrack", "MoveTrack", "PositionTrack",
        "TileDimensions", "SetFloorIcon",
        "MoveDecorations", "SetText", "EmitParticle", "SetParticle", "SetObject", "SetDefaultText",
        "CustomBackground", "Flash", "MoveCamera", "SetFilter", "SetFilterAdvanced",
        "HallOfMirrors", "ShakeScreen", "Bloom", "ScreenTile", "ScreenScroll", "SetFrameRate",
        "RepeatEvents", "SetConditionalEvents", "SetInputEvent",
        "EditorComment", "Bookmark", "CallMethod", "AddComponent",
        "Hold", "SetHoldSound", "MultiPlanet", "FreeRoam", "FreeRoamTwirl",
        "FreeRoamRemove", "Hide", "ScaleMargin", "ScaleRadius"
    ];

    private static readonly string[] ProOnlyTypes =
    [
        "Multitap", "KillPlayer", "SetFloorIcon", "CallMethod", "AddComponent"
    ];

    private static readonly string[] NeoCosmosTypes =
    [
        "Hold", "SetHoldSound", "MultiPlanet", "FreeRoam", "FreeRoamTwirl",
        "FreeRoamRemove", "Hide", "ScaleMargin", "ScaleRadius"
    ];

    public static void Run()
    {
        MethodInfo method = typeof(MainWindow).GetMethod(
            "IsEventAvailable",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(bool), typeof(bool), typeof(bool)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "MainWindow.IsEventAvailable(eventType, enableProEvents, hasNeoCosmos, isOfficialLevel) is missing.");

        AssertVisibleCount(method, enableProEvents: false, hasNeoCosmos: false, isOfficialLevel: false, expected: 36, "standard retail");
        AssertVisibleCount(method, enableProEvents: true, hasNeoCosmos: false, isOfficialLevel: false, expected: 41, "pro/dev gate");
        AssertVisibleCount(method, enableProEvents: false, hasNeoCosmos: true, isOfficialLevel: false, expected: 45, "Neo Cosmos");
        AssertVisibleCount(method, enableProEvents: true, hasNeoCosmos: true, isOfficialLevel: false, expected: 51, "pro/dev + Neo Cosmos");
        AssertVisibleCount(method, enableProEvents: false, hasNeoCosmos: false, isOfficialLevel: true, expected: 51, "official level");

        foreach (string eventType in ProOnlyTypes)
        {
            AssertAvailability(method, eventType, false, false, false, expected: false, "standard retail must hide pro/dev events");
            AssertAvailability(method, eventType, true, false, false, expected: true, "pro/dev gate must expose pro events");
        }

        foreach (string eventType in NeoCosmosTypes)
        {
            AssertAvailability(method, eventType, false, false, false, expected: false, "standard retail must hide Neo Cosmos events");
            AssertAvailability(method, eventType, false, true, false, expected: true, "Neo Cosmos must expose DLC events");
        }

        AssertAvailability(method, "TileDimensions", false, false, false, expected: false, "TileDimensions must be gated in standard retail");
        AssertAvailability(method, "TileDimensions", true, false, false, expected: false, "TileDimensions requires Neo Cosmos as well as the pro/dev gate");
        AssertAvailability(method, "TileDimensions", false, true, false, expected: false, "TileDimensions requires the pro/dev gate as well as Neo Cosmos");
        AssertAvailability(method, "TileDimensions", true, true, false, expected: true, "TileDimensions must appear when both gates are satisfied");
        AssertAvailability(method, "TileDimensions", false, false, true, expected: true, "official levels bypass event gates");

        AssertAvailability(method, "SetFilterAdvanced", false, false, false, expected: true, "SetFilterAdvanced is a standard current event");
        AssertAvailability(method, "KillPlayer", false, false, true, expected: true, "official levels must expose pro/dev-gated events");
        AssertAvailability(method, "Hold", false, false, true, expected: true, "official levels must expose Neo Cosmos-gated events");
    }

    private static void AssertVisibleCount(
        MethodInfo method,
        bool enableProEvents,
        bool hasNeoCosmos,
        bool isOfficialLevel,
        int expected,
        string scenario)
    {
        int actual = AllAddEventTypes.Count(eventType =>
            Invoke(method, eventType, enableProEvents, hasNeoCosmos, isOfficialLevel));
        if (actual != expected)
            throw new InvalidOperationException($"{scenario} Add Event visibility must expose {expected} events, not {actual}.");
    }

    private static void AssertAvailability(
        MethodInfo method,
        string eventType,
        bool enableProEvents,
        bool hasNeoCosmos,
        bool isOfficialLevel,
        bool expected,
        string message)
    {
        bool actual = Invoke(method, eventType, enableProEvents, hasNeoCosmos, isOfficialLevel);
        if (actual != expected)
            throw new InvalidOperationException($"{message}: {eventType} returned {actual}.");
    }

    private static bool Invoke(
        MethodInfo method,
        string eventType,
        bool enableProEvents,
        bool hasNeoCosmos,
        bool isOfficialLevel)
        => (bool)(method.Invoke(null, [eventType, enableProEvents, hasNeoCosmos, isOfficialLevel])
            ?? throw new InvalidOperationException($"Availability evaluation returned null for {eventType}."));
}
