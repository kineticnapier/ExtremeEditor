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

    // ExtremeEditor models the retail editor context. Pro/official gates remain
    // disabled, while Neo Cosmos availability follows the detected ADOFAI install.
    private static bool EnableProEvents => false;
    private bool HasNeoCosmos => _hasNeoCosmos;
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

    private bool IsEventAvailableForCurrentEditor(string eventType) =>
        IsEventAvailable(eventType, EnableProEvents, HasNeoCosmos, IsOfficialLevel);

    private EventCategoryDefinition GetVisibleEventCategory(EventCategoryDefinition category) =>
        category with
        {
            Events = category.Events
                .Where(IsEventAvailableForCurrentEditor)
                .ToArray()
        };

    private EventCatalogEntry[] BuildVisibleEventCatalog() =>
        EventCatalog
            .Where(entry => IsEventAvailableForCurrentEditor(entry.EventType))
            .ToArray();
}
