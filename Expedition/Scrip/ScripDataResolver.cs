using System.Reflection;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Expedition.RecipeResolver;

namespace Expedition.Scrip;

/// <summary>
/// Parses Lumina game data sheets to build per-ScripType collectable lists.
/// Cross-references CollectablesShopItem with Recipe and GatheringItem sheets.
/// Uses reflection to access CollectablesShopRefine properties which may vary
/// across Lumina versions.
/// </summary>
public sealed class ScripDataResolver
{
    private readonly Dictionary<ScripType, List<ScripCollectableInfo>> collectablesByType = new();
    private bool isInitialized;

    /// <summary>
    /// Lazily initializes and returns the collectable list for the given scrip type.
    /// </summary>
    public IReadOnlyList<ScripCollectableInfo> GetCollectables(ScripType scripType)
    {
        EnsureInitialized();
        return collectablesByType.TryGetValue(scripType, out var list) ? list : Array.Empty<ScripCollectableInfo>();
    }

    /// <summary>
    /// Returns the best collectable for the given scrip type and player level.
    /// Prefers non-timed-node collectables to avoid idle waiting. Falls back to
    /// timed nodes only if no regular-node options exist.
    /// </summary>
    public ScripCollectableInfo? RecommendBest(ScripType scripType, int playerLevel)
    {
        var collectables = GetCollectables(scripType);
        var eligible = collectables
            .Where(c => scripType.IsCrafter() ? c.RequiredLevel <= playerLevel : c.GatherLevel <= playerLevel)
            .ToList();

        // Prefer non-timed-node collectables (always available, no idle waiting)
        var nonTimed = eligible.Where(c => !c.IsTimedNode).ToList();
        var pool = nonTimed.Count > 0 ? nonTimed : eligible;

        return pool
            .OrderByDescending(c => c.ScripReward)
            .ThenByDescending(c => scripType.IsCrafter() ? c.RequiredLevel : c.GatherLevel)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns all gatherer collectables for the given scrip type (both MIN and BTN),
    /// filtered by player level. Used for building auto-rotation pools.
    /// </summary>
    public IReadOnlyList<ScripCollectableInfo> GetAllGathererCollectables(ScripType scripType, int playerLevel)
    {
        var collectables = GetCollectables(scripType);
        return collectables
            .Where(c => c.IsGatherable && c.GatherLevel <= playerLevel)
            .OrderByDescending(c => c.ScripReward)
            .ToList();
    }

    private void EnsureInitialized()
    {
        if (isInitialized) return;
        isInitialized = true;

        try
        {
            BuildCollectableDatabase();
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[ScripDataResolver] Failed to build collectable database.");
        }
    }

    private void BuildCollectableDatabase()
    {
        foreach (ScripType st in Enum.GetValues(typeof(ScripType)))
            collectablesByType[st] = new List<ScripCollectableInfo>();

        var itemSheet = DalamudApi.DataManager.GetExcelSheet<Item>();
        var recipeSheet = DalamudApi.DataManager.GetExcelSheet<Recipe>();
        var gatheringItemSheet = DalamudApi.DataManager.GetExcelSheet<GatheringItem>();

        if (itemSheet == null || recipeSheet == null || gatheringItemSheet == null)
        {
            DalamudApi.Log.Warning("[ScripDataResolver] Missing required Lumina sheets.");
            return;
        }

        // Build recipe lookup: itemId -> Recipe
        var recipeByItemId = new Dictionary<uint, Recipe>();
        foreach (var recipe in recipeSheet)
        {
            if (recipe.ItemResult.RowId > 0 && !recipeByItemId.ContainsKey(recipe.ItemResult.RowId))
                recipeByItemId[recipe.ItemResult.RowId] = recipe;
        }

        // Build gathering item lookup: itemId -> GatheringItem
        var gatheringByItemId = new Dictionary<uint, GatheringItem>();
        foreach (var gi in gatheringItemSheet)
        {
            if (gi.Item.RowId > 0 && !gatheringByItemId.ContainsKey(gi.Item.RowId))
                gatheringByItemId[gi.Item.RowId] = gi;
        }

        // Build GatherType lookup: GatheringItem RowId -> GatherType (MIN vs BTN)
        var gatherTypeLookup = BuildGatherTypeLookup();

        // Build timed node detection: Item RowId -> spawn data for timed/unspoiled nodes
        var timedNodeData = BuildTimedNodeData(gatheringByItemId);

        // Parse CollectablesShopItem subrow sheet
        var shopSubSheet = DalamudApi.DataManager.GetSubrowExcelSheet<CollectablesShopItem>();
        if (shopSubSheet == null)
        {
            DalamudApi.Log.Warning("[ScripDataResolver] CollectablesShopItem sheet not found.");
            return;
        }

        // Probe CollectablesShopRefine property names via reflection
        // (Lumina generated sheet property names can vary by version)
        var refineSheet = DalamudApi.DataManager.GetExcelSheet<CollectablesShopRefine>();
        var refineProps = DiscoverRefineProperties(refineSheet);

        foreach (var parentRow in shopSubSheet)
        {
            foreach (var shopItem in parentRow)
            {
                var itemId = shopItem.Item.RowId;
                if (itemId == 0) continue;

                var item = itemSheet.GetRowOrDefault(itemId);
                if (item == null) continue;
                var itemValue = item.Value;

                var itemName = itemValue.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(itemName)) continue;

                // Try to get scrip reward from CollectablesShopRefine
                var (scripReward, collectabilityMin) = GetRefineData(
                    refineSheet, shopItem.CollectablesShopRefine.RowId, refineProps);

                // If we couldn't get refine data, estimate from item level
                if (scripReward <= 0)
                    scripReward = EstimateScripReward(itemValue);
                if (collectabilityMin <= 0)
                    collectabilityMin = 1;

                if (scripReward <= 0) continue;

                // Determine ScripType
                var isCrafter = recipeByItemId.ContainsKey(itemId);
                var isGatherer = gatheringByItemId.ContainsKey(itemId);
                var scripType = ClassifyScripType(isCrafter, isGatherer, itemValue);
                if (scripType == null) continue;

                var recipeId = 0u;
                var craftTypeId = -1;
                var requiredLevel = 0;
                if (isCrafter && recipeByItemId.TryGetValue(itemId, out var recipe))
                {
                    recipeId = recipe.RowId;
                    craftTypeId = (int)recipe.CraftType.RowId;
                    requiredLevel = (int)recipe.RecipeLevelTable.Value.ClassJobLevel;
                }

                var gatherType = GatherType.None;
                var gatherLevel = 0;
                var isTimedNode = false;
                var spawnHours = Array.Empty<int>();
                var spawnDuration = 2;
                if (isGatherer && gatheringByItemId.TryGetValue(itemId, out var gatherItem))
                {
                    // Look up actual GatherType from GatheringPointBase scan
                    gatherType = gatherTypeLookup.TryGetValue(gatherItem.RowId, out var gt)
                        ? gt
                        : GatherType.Miner; // Fallback if lookup misses
                    gatherLevel = (int)gatherItem.GatheringItemLevel.Value.GatheringItemLevel;
                    if (timedNodeData.TryGetValue(itemId, out var tnd))
                    {
                        isTimedNode = true;
                        spawnHours = tnd.SpawnHours;
                        spawnDuration = tnd.DurationHours;
                    }
                }

                var info = new ScripCollectableInfo
                {
                    ItemId = itemId,
                    ItemName = itemName,
                    IconId = itemValue.Icon,
                    ScripType = scripType.Value,
                    ScripReward = scripReward,
                    CollectabilityMin = collectabilityMin,
                    RecipeId = recipeId,
                    CraftTypeId = craftTypeId,
                    RequiredLevel = requiredLevel,
                    GatherType = gatherType,
                    GatherLevel = gatherLevel,
                    IsTimedNode = isTimedNode,
                    SpawnHours = spawnHours,
                    SpawnDurationHours = spawnDuration,
                };

                collectablesByType[scripType.Value].Add(info);
            }
        }

        // Sort each list by scrip reward descending
        foreach (var (_, list) in collectablesByType)
            list.Sort((a, b) => b.ScripReward.CompareTo(a.ScripReward));

        var total = collectablesByType.Values.Sum(l => l.Count);
        DalamudApi.Log.Information($"[ScripDataResolver] Built database: {total} collectables across {collectablesByType.Count} scrip types.");
    }

    // --- GatherType detection ---

    /// <summary>
    /// Scans GatheringPointBase to determine whether each GatheringItem is MIN or BTN.
    /// GatheringType values: 0,1 = Mining → GatherType.Miner; 2,3 = Botany → GatherType.Botanist.
    /// Returns a map of GatheringItem RowId → GatherType.
    /// </summary>
    private static Dictionary<uint, GatherType> BuildGatherTypeLookup()
    {
        var result = new Dictionary<uint, GatherType>();

        try
        {
            var gpbSheet = DalamudApi.DataManager.GetExcelSheet<GatheringPointBase>();
            if (gpbSheet == null) return result;

            foreach (var gpb in gpbSheet)
            {
                // GatheringType: 0=Mining, 1=Quarrying (MIN), 2=Logging, 3=Harvesting (BTN)
                var gt = (int)gpb.GatheringType.RowId <= 1 ? GatherType.Miner : GatherType.Botanist;

                for (var i = 0; i < gpb.Item.Count; i++)
                {
                    var giRowId = gpb.Item[i].RowId;
                    if (giRowId != 0 && !result.ContainsKey(giRowId))
                        result[giRowId] = gt;
                }
            }

            DalamudApi.Log.Information($"[ScripDataResolver] GatherType lookup: {result.Count} gathering items mapped.");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[ScripDataResolver] Failed to build GatherType lookup.");
        }

        return result;
    }

    // --- Timed node detection ---

    private record TimedNodeInfo(int[] SpawnHours, int DurationHours);

    /// <summary>
    /// Builds a map of Item RowId → spawn data for items gathered from timed/unspoiled nodes.
    /// Uses GatheringPoint → GatheringPointBase (items) + GatheringPointTransient (spawn times)
    /// + GatheringRarePopTimeTable (exact spawn hours for unspoiled nodes).
    /// </summary>
    private static Dictionary<uint, TimedNodeInfo> BuildTimedNodeData(Dictionary<uint, GatheringItem> gatheringByItemId)
    {
        var result = new Dictionary<uint, TimedNodeInfo>();

        try
        {
            var gpSheet = DalamudApi.DataManager.GetExcelSheet<GatheringPoint>();
            var gpbSheet = DalamudApi.DataManager.GetExcelSheet<GatheringPointBase>();
            var transientSheet = DalamudApi.DataManager.GetExcelSheet<GatheringPointTransient>();
            var rarePopSheet = DalamudApi.DataManager.GetExcelSheet<GatheringRarePopTimeTable>();

            if (gpSheet == null || gpbSheet == null || transientSheet == null)
            {
                DalamudApi.Log.Warning("[ScripDataResolver] Missing sheets for timed node detection.");
                return result;
            }

            // Build reverse lookup: GatheringItem RowId -> Item RowId
            var gatheringItemToItemId = new Dictionary<uint, uint>();
            foreach (var (itemId, gi) in gatheringByItemId)
                gatheringItemToItemId[gi.RowId] = itemId;

            // Scan GatheringPoint rows for timed nodes
            foreach (var gp in gpSheet)
            {
                var gpBaseId = gp.GatheringPointBase.RowId;
                if (gpBaseId == 0) continue;

                // Check if this gathering point has a transient entry (timed/unspoiled/ephemeral)
                var transient = transientSheet.GetRowOrDefault(gp.RowId);
                if (transient == null) continue;

                var t = transient.Value;
                var rarePopId = t.GatheringRarePopTimeTable.RowId;
                var isUnspoiled = rarePopId != 0;
                var isEphemeral = t.EphemeralStartTime != t.EphemeralEndTime && t.EphemeralStartTime != 65535;

                if (!isUnspoiled && !isEphemeral) continue;

                // Extract spawn hours
                int[] spawnHours;
                int duration;

                if (isUnspoiled && rarePopSheet != null)
                {
                    // Read exact spawn hours from GatheringRarePopTimeTable
                    var rarePop = rarePopSheet.GetRowOrDefault(rarePopId);
                    if (rarePop != null)
                    {
                        var hours = new List<int>();
                        var rpValue = rarePop.Value;
                        for (var s = 0; s < rpValue.StartTime.Count; s++)
                        {
                            var dur = rpValue.Duration[s];
                            if (dur == 0) continue;
                            // StartTime is stored as ET hour * 100 (e.g., 400 = ET 4:00)
                            hours.Add((int)rpValue.StartTime[s] / 100);
                        }
                        spawnHours = hours.ToArray();
                        // Duration is stored as ET hours * 100 (e.g., 160 = ~2h). Use
                        // ceiling division to avoid truncation (160/100 = 1 is wrong, should be 2).
                        duration = spawnHours.Length > 0 && rpValue.Duration[0] > 0
                            ? (int)Math.Ceiling(rpValue.Duration[0] / 100.0)
                            : 2;
                        if (duration <= 0) duration = 2;
                    }
                    else
                    {
                        spawnHours = Array.Empty<int>();
                        duration = 2;
                    }
                }
                else
                {
                    // Ephemeral: spawns every 4 ET hours
                    spawnHours = new[] { (int)t.EphemeralStartTime };
                    var end = (int)t.EphemeralEndTime;
                    duration = end >= t.EphemeralStartTime
                        ? end - (int)t.EphemeralStartTime
                        : 24 - (int)t.EphemeralStartTime + end;
                    if (duration <= 0) duration = 4;
                }

                if (spawnHours.Length == 0) continue;

                var nodeInfo = new TimedNodeInfo(spawnHours, duration);

                // Get the GatheringPointBase to find which items are on this node
                var gpBase = gpbSheet.GetRowOrDefault(gpBaseId);
                if (gpBase == null) continue;

                for (var i = 0; i < gpBase.Value.Item.Count; i++)
                {
                    var giRowId = gpBase.Value.Item[i].RowId;
                    if (giRowId == 0) continue;

                    if (gatheringItemToItemId.TryGetValue(giRowId, out var itemId) && !result.ContainsKey(itemId))
                        result[itemId] = nodeInfo;
                }
            }

            DalamudApi.Log.Information($"[ScripDataResolver] Identified {result.Count} items from timed/unspoiled nodes.");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[ScripDataResolver] Failed to build timed node data.");
        }

        return result;
    }

    // --- CollectablesShopRefine reflection ---

    private record RefinePropertyMap(
        PropertyInfo? LowCollectability,
        PropertyInfo? HighCollectability,
        PropertyInfo? LowReward,
        PropertyInfo? HighReward);

    private static RefinePropertyMap? DiscoverRefineProperties(ExcelSheet<CollectablesShopRefine>? sheet)
    {
        if (sheet == null) return null;

        // Get a sample row to probe property names
        CollectablesShopRefine? sample = null;
        foreach (var row in sheet)
        {
            sample = row;
            break;
        }
        if (sample == null) return null;

        var type = typeof(CollectablesShopRefine);
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // Try known property name patterns across Lumina versions
        var lowCollNames = new[] { "LowCollectability", "CollectabilityLow", "Unknown0", "LowCollectabillity" };
        var highCollNames = new[] { "HighCollectability", "CollectabilityHigh", "Unknown2", "HighCollectabillity" };
        var lowRewardNames = new[] { "LowReward", "RewardLow", "Unknown1", "LowScrip" };
        var highRewardNames = new[] { "HighReward", "RewardHigh", "Unknown3", "HighScrip" };

        var lowColl = FindProp(props, lowCollNames);
        var highColl = FindProp(props, highCollNames);
        var lowReward = FindProp(props, lowRewardNames);
        var highReward = FindProp(props, highRewardNames);

        if (lowReward == null && highReward == null)
        {
            // Log all available properties for debugging
            var propNames = string.Join(", ", props.Select(p => $"{p.Name}:{p.PropertyType.Name}"));
            DalamudApi.Log.Warning($"[ScripDataResolver] CollectablesShopRefine properties: {propNames}");
            DalamudApi.Log.Warning("[ScripDataResolver] Could not find reward properties — will estimate scrip values.");
        }
        else
        {
            DalamudApi.Log.Information($"[ScripDataResolver] Refine properties: " +
                $"LowColl={lowColl?.Name}, HighColl={highColl?.Name}, " +
                $"LowReward={lowReward?.Name}, HighReward={highReward?.Name}");
        }

        return new RefinePropertyMap(lowColl, highColl, lowReward, highReward);
    }

    private static PropertyInfo? FindProp(PropertyInfo[] props, string[] candidates)
    {
        foreach (var name in candidates)
        {
            var p = props.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p != null) return p;
        }
        return null;
    }

    private static (int ScripReward, int CollectabilityMin) GetRefineData(
        ExcelSheet<CollectablesShopRefine>? sheet, uint refineId, RefinePropertyMap? props)
    {
        if (sheet == null || props == null || refineId == 0)
            return (0, 0);

        var row = sheet.GetRowOrDefault(refineId);
        if (row == null) return (0, 0);

        var refine = row.Value;
        var reward = 0;
        var collectability = 0;

        if (props.HighReward != null)
        {
            try { reward = Convert.ToInt32(props.HighReward.GetValue(refine)); } catch { }
        }
        if (reward <= 0 && props.LowReward != null)
        {
            try { reward = Convert.ToInt32(props.LowReward.GetValue(refine)); } catch { }
        }

        if (props.LowCollectability != null)
        {
            try { collectability = Convert.ToInt32(props.LowCollectability.GetValue(refine)); } catch { }
        }

        return (reward, collectability);
    }

    // --- Fallback estimations ---

    private static int EstimateScripReward(Item item)
    {
        // Estimate scrip reward based on item level
        // This is a rough heuristic — actual values come from CollectablesShopRefine
        var ilvl = item.LevelItem.RowId;
        return ilvl switch
        {
            >= 710 => 198, // Dawntrail endgame orange
            >= 690 => 144, // Dawntrail orange
            >= 620 => 108, // Endwalker high
            >= 560 => 72,  // Endwalker mid
            >= 500 => 54,  // Shadowbringers
            >= 400 => 36,  // Stormblood
            _ => 18,       // Lower level
        };
    }

    private static ScripType? ClassifyScripType(bool isCrafter, bool isGatherer, Item item)
    {
        var ilvl = item.LevelItem.RowId;

        if (isCrafter)
            return ilvl >= 690 ? ScripType.OrangeCrafter : ScripType.PurpleCrafter;

        if (isGatherer)
            return ilvl >= 690 ? ScripType.OrangeGatherer : ScripType.PurpleGatherer;

        return null;
    }
}
