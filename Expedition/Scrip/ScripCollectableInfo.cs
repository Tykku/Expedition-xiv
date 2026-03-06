using Expedition.RecipeResolver;

namespace Expedition.Scrip;

/// <summary>
/// Describes a single collectable item that can be turned in for scrips.
/// Built from Lumina CollectablesShopItem + Recipe/GatheringItem cross-references.
/// </summary>
public sealed record ScripCollectableInfo
{
    // Base identity
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint IconId { get; init; }
    public ScripType ScripType { get; init; }
    public int ScripReward { get; init; }
    public int CollectabilityMin { get; init; }

    // Crafter fields (populated when IsCraftable)
    public uint RecipeId { get; init; }
    public int CraftTypeId { get; init; } = -1;
    public int RequiredLevel { get; init; }

    // Gatherer fields (populated when IsGatherable)
    public GatherType GatherType { get; init; }
    public int GatherLevel { get; init; }
    public bool IsTimedNode { get; init; }
    public int[] SpawnHours { get; init; } = Array.Empty<int>();
    public int SpawnDurationHours { get; init; } = 2;

    // Computed
    public bool IsCraftable => CraftTypeId >= 0;
    public bool IsGatherable => GatherType is GatherType.Miner or GatherType.Botanist;

    public override string ToString() => $"{ItemName} ({ScripReward} scrip)";
}
