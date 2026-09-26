namespace ExtremeEditor.Wpf;

public partial class MainWindow
{
    private static readonly HashSet<string> ProEventTypes = new(StringComparer.Ordinal)
    {
        "Multitap",
        "KillPlayer",
        "SetFloorIcon",
        "CallMethod",
        "AddComponent"
    };

    private static readonly HashSet<string> NeoCosmosEventTypes = new(StringComparer.Ordinal)
    {
        "Hold",
        "SetHoldSound",
        "MultiPlanet",
        "FreeRoam",
        "FreeRoamTwirl",
        "FreeRoamRemove",
        "Hide",
        "ScaleMargin",
        "ScaleRadius"
    };

    // ExtremeEditor currently models the normal retail editor context. These three
    // accessors keep the gate decision centralized so DLC/official detection can be
    // wired later without duplicating visibility logic throughout the picker.
    private static bool EnableProEvents => false;
    private static bool HasNeoCosmos => false;
    private static bool IsOfficialLevel => false;

    private static bool IsEventAvailable(
        string eventType,
        bool enableProEvents,
        bool hasNeoCosmos,
        bool isOfficialLevel)
    {
        if (isOfficialLevel)
            return true;

        bool requiresPro = ProEventTypes.Contains(eventType) ||
            string.Equals(eventType, "TileDimensions", StringComparison.Ordinal);
        bool requiresNeoCosmos = NeoCosmosEventTypes.Contains(eventType) ||
            string.Equals(eventType, "TileDimensions", StringComparison.Ordinal);

        return (!requiresPro || enableProEvents) &&
               (!requiresNeoCosmos || hasNeoCosmos);
    }

    private static bool IsEventAvailableForCurrentEditor(string eventType) =>
        IsEventAvailable(eventType, EnableProEvents, HasNeoCosmos, IsOfficialLevel);

    private static EventCategoryDefinition GetVisibleEventCategory(EventCategoryDefinition category) =>
        category with
        {
            Events = category.Events
                .Where(IsEventAvailableForCurrentEditor)
                .ToArray()
        };

    private static EventCatalogEntry[] BuildVisibleEventCatalog() =>
        EventCatalog
            .Where(entry => IsEventAvailableForCurrentEditor(entry.EventType))
            .ToArray();
}
