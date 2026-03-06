using FFXIVClientStructs.FFXIV.Client.Game;

namespace Expedition.Scrip;

/// <summary>
/// Scrip currency types matching GBR's CollectableManager enum ordering.
/// </summary>
public enum ScripType
{
    PurpleCrafter = 0,
    PurpleGatherer = 1,
    OrangeCrafter = 2,
    OrangeGatherer = 3,
}

public static class ScripTypeExtensions
{
    /// <summary>
    /// Returns the in-game currency item ID for the given scrip type.
    /// Purple Crafters' Scrip = 33913, Purple Gatherers' Scrip = 33914,
    /// Orange Crafters' Scrip = 41784, Orange Gatherers' Scrip = 41785.
    /// </summary>
    public static uint CurrencyItemId(this ScripType type) => type switch
    {
        ScripType.PurpleCrafter => 33913,
        ScripType.PurpleGatherer => 33914,
        ScripType.OrangeCrafter => 41784,
        ScripType.OrangeGatherer => 41785,
        _ => 0,
    };

    public static string DisplayName(this ScripType type) => type switch
    {
        ScripType.PurpleCrafter => "Purple Crafter",
        ScripType.PurpleGatherer => "Purple Gatherer",
        ScripType.OrangeCrafter => "Orange Crafter",
        ScripType.OrangeGatherer => "Orange Gatherer",
        _ => "Unknown",
    };

    public static bool IsCrafter(this ScripType type) =>
        type is ScripType.PurpleCrafter or ScripType.OrangeCrafter;

    public static bool IsGatherer(this ScripType type) =>
        type is ScripType.PurpleGatherer or ScripType.OrangeGatherer;

    /// <summary>
    /// Reads the current scrip balance from the game's special currency inventory.
    /// </summary>
    public static unsafe int GetBalance(this ScripType type)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;
        return manager->GetInventoryItemCount(type.CurrencyItemId());
    }
}
