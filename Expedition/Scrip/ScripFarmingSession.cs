using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Expedition.Crafting;
using Expedition.Gathering;
using Expedition.IPC;
using Expedition.RecipeResolver;
using Expedition.Scheduling;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Expedition.Scrip;

/// <summary>
/// State machine for end-to-end scrip farming: gather -> craft (if crafter) -> turn-in -> repeat.
/// Delegates turn-in to GBR's CollectableManager via reflection.
/// </summary>
public sealed class ScripFarmingSession : IDisposable
{
    // --- State machine ---

    public enum SessionState
    {
        Idle,
        Initializing,
        GatheringMaterials,
        Crafting,
        WaitingForTurnIn,
        CheckingGoal,
        Completed,
        Error,
        Paused,
    }

    public SessionState State { get; private set; } = SessionState.Idle;
    public string StatusMessage { get; private set; } = string.Empty;
    public bool IsActive => State is not SessionState.Idle and not SessionState.Completed and not SessionState.Error;

    // --- Config ---

    public ScripType ScripType { get; set; } = ScripType.OrangeCrafter;
    public ScripCollectableInfo? SelectedCollectable { get; set; }
    public int Goal { get; set; } = 4000;
    public int BatchSize { get; set; } = 10;

    // --- Session tracking ---

    public DateTime? StartTime { get; private set; }
    public int StartingBalance { get; private set; }
    public int LoopCount { get; private set; }

    // --- Dependencies ---

    private readonly IpcManager ipc;
    private readonly GatheringOrchestrator gatheringOrchestrator;
    private readonly CraftingOrchestrator craftingOrchestrator;
    private readonly RecipeResolverService recipeResolver;

    // --- Throttling ---

    private DateTime lastUpdate = DateTime.MinValue;
    private const double UpdateIntervalSec = 0.5;

    // --- Turn-in reflection ---

    private const BindingFlags AllFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private object? collectableManagerInstance;
    private PropertyInfo? isRunningProp;
    private MethodInfo? startMethod;
    private MethodInfo? stopMethod;
    private MethodInfo? hasCollectablesMethod;
    private PropertyInfo? autoTurnInProp;
    private bool collectableManagerInitialized;
    private bool turnInStarted;
    private DateTime turnInStartTime;
    private DateTime turnInReadyTime;
    private const double TurnInTimeoutSec = 300; // 5 minutes
    private const double TurnInDelaySec = 2.0; // brief delay after gathering stops before turn-in

    // --- Smart timed-node gathering ---

    /// <summary>Non-timed fallback collectable used while waiting for timed node windows.</summary>
    public ScripCollectableInfo? FallbackCollectable { get; private set; }

    /// <summary>Which collectable is currently being gathered (primary or fallback).</summary>
    private ScripCollectableInfo? currentGatherTarget;

    /// <summary>True if we're currently gathering the fallback instead of the primary.</summary>
    public bool IsGatheringFallback => currentGatherTarget != null && FallbackCollectable != null
        && currentGatherTarget.ItemId == FallbackCollectable.ItemId;

    // --- Auto-rotation (cross-class timed node chasing) ---

    /// <summary>All eligible timed collectables sorted by scrip reward desc (both MIN and BTN).</summary>
    private List<ScripCollectableInfo> rotationPool = new();

    /// <summary>Best non-timed collectable for fallback during rotation.</summary>
    private ScripCollectableInfo? bestNonTimedFallback;

    /// <summary>All top-tier non-timed fallbacks (same scrip reward) across both MIN and BTN.
    /// Randomly picked between on each fallback switch to vary the job being used.</summary>
    private List<ScripCollectableInfo> nonTimedFallbackPool = new();

    private static readonly Random rng = new();

    /// <summary>Whether auto-rotation across timed nodes is active.</summary>
    public bool IsAutoRotation { get; private set; }

    /// <summary>The rotation pool for UI display.</summary>
    public IReadOnlyList<ScripCollectableInfo> RotationPool => rotationPool;

    /// <summary>The current gather target for UI display.</summary>
    public ScripCollectableInfo? CurrentGatherTarget => currentGatherTarget;

    // --- Rotation switch cooldown ---

    /// <summary>Timestamp of last rotation switch. Prevents rapid flip-flopping mid-node.</summary>
    private DateTime lastRotationSwitchTime = DateTime.MinValue;

    /// <summary>Minimum real seconds between rotation switches. Gives GBR time to finish
    /// the current node interaction cycle before we yank the gather list.</summary>
    private const double RotationSwitchCooldownSec = 30.0;

    /// <summary>
    /// Tracks when each timed node was last farmed (by ItemId → real UTC time).
    /// Static so it persists across sessions within the same plugin lifetime.
    /// Used to avoid re-farming a timed node whose window is still active from a previous session.
    /// </summary>
    private static readonly Dictionary<uint, DateTime> lastFarmedTime = new();

    // --- Gather/Craft state ---

    private bool gatheringStarted;
    private bool craftingStarted;
    private int baselineInventoryCount;

    // --- Pre-craft teleport ---
    private bool preCraftTeleportSent;
    private bool preCraftTeleportArrived;
    private DateTime preCraftTeleportSentTime;
    private const double TeleportTimeoutSec = 30.0;

    // Max items per batch — limited by inventory space (collectables don't stack,
    // each takes one slot; 4 inventory pages × 35 slots = 140, leave room for other items).
    private const int MaxBatchSize = 99;

    /// <summary>
    /// Calculates the effective batch size for the current loop.
    /// Scales up to however many items are needed to reach the scrip goal in one go,
    /// using BatchSize as the minimum. Capped at MaxBatchSize for inventory sanity.
    /// </summary>
    private int EffectiveBatchSize
    {
        get
        {
            if (SelectedCollectable == null || SelectedCollectable.ScripReward <= 0)
                return BatchSize;

            var remaining = Goal - CurrentBalance;
            if (remaining <= 0) return 1; // Goal already met, minimal batch

            var itemsNeeded = (int)Math.Ceiling((double)remaining / SelectedCollectable.ScripReward);
            // Use at least BatchSize (user's configured minimum per loop),
            // scale up to itemsNeeded to finish in one loop when possible.
            return Math.Clamp(itemsNeeded, 1, MaxBatchSize);
        }
    }

    // --- Stall detection ---

    private int lastKnownGatherCount;
    private DateTime lastGatherProgressTime;
    private const double GatherStallTimeoutSec = 120; // 2 minutes with no progress = stalled

    // --- Activity log ---

    private readonly List<string> activityLog = new();
    public IReadOnlyList<string> ActivityLog => activityLog;

    public ScripFarmingSession(
        IpcManager ipc,
        GatheringOrchestrator gatheringOrchestrator,
        CraftingOrchestrator craftingOrchestrator,
        RecipeResolverService recipeResolver)
    {
        this.ipc = ipc;
        this.gatheringOrchestrator = gatheringOrchestrator;
        this.craftingOrchestrator = craftingOrchestrator;
        this.recipeResolver = recipeResolver;
    }

    // --- Public API ---

    public void Start(ScripCollectableInfo collectable, bool isAutoMode = false)
    {
        if (State is not SessionState.Idle and not SessionState.Completed and not SessionState.Error)
        {
            Log("Cannot start: session already active.");
            return;
        }

        // Mutual exclusion with WorkflowEngine
        var workflowEngine = Expedition.Instance?.WorkflowEngine;
        if (workflowEngine != null && workflowEngine.CurrentState != Workflow.WorkflowState.Idle)
        {
            Log("Cannot start: WorkflowEngine is running. Stop it first.");
            TransitionTo(SessionState.Error, "WorkflowEngine is active — stop it first.");
            return;
        }

        SelectedCollectable = collectable;
        FallbackCollectable = null;
        currentGatherTarget = null;
        rotationPool.Clear();
        bestNonTimedFallback = null;
        IsAutoRotation = false;
        StartTime = DateTime.UtcNow;
        StartingBalance = ScripType.GetBalance();
        LoopCount = 0;
        activityLog.Clear();
        gatheringStarted = false;
        craftingStarted = false;
        turnInStarted = false;

        var resolver = Expedition.Instance?.ScripDataResolver;
        var player = DalamudApi.ObjectTable.LocalPlayer;
        var level = (int)(player?.Level ?? 100);

        if (isAutoMode && !ScripType.IsCrafter() && resolver != null)
        {
            // Auto-rotation mode: chase timed nodes across MIN and BTN that beat the fallback
            IsAutoRotation = true;
            var allGatherer = resolver.GetAllGathererCollectables(ScripType, level);

            // Pick best non-timed fallback first (prefer highest scrip, then highest level)
            bestNonTimedFallback = allGatherer.Where(c => !c.IsTimedNode)
                .OrderByDescending(c => c.ScripReward)
                .ThenByDescending(c => c.GatherLevel)
                .FirstOrDefault();

            // Build a pool of top-tier non-timed fallbacks across both MIN and BTN
            // so we can randomly alternate jobs when falling back (looks less suspicious).
            if (bestNonTimedFallback != null)
            {
                var topReward = bestNonTimedFallback.ScripReward;
                nonTimedFallbackPool = allGatherer
                    .Where(c => !c.IsTimedNode && c.ScripReward == topReward)
                    .GroupBy(c => c.GatherType) // one per job type
                    .Select(g => g.OrderByDescending(c => c.GatherLevel).First())
                    .ToList();
            }

            // Only track timed nodes that give at least as much scrip as the fallback.
            // Use >= because timed/non-timed at the same tier often share the same reward value,
            // and timed nodes are still worth chasing (guaranteed spawns, variety prevents GBR loops).
            // Also require gather level within 10 of player to avoid old-expansion zones.
            var fallbackReward = bestNonTimedFallback?.ScripReward ?? 0;
            var minGatherLevel = Math.Max(1, level - 10);
            rotationPool = allGatherer
                .Where(c => c.IsTimedNode && c.ScripReward >= fallbackReward && c.GatherLevel >= minGatherLevel)
                .OrderByDescending(c => c.ScripReward)
                .ThenByDescending(c => c.GatherLevel)
                .ToList();

            var minCount = rotationPool.Count(c => c.GatherType == GatherType.Miner);
            var btnCount = rotationPool.Count(c => c.GatherType == GatherType.Botanist);
            Log($"Auto-rotation: tracking {rotationPool.Count} timed nodes ({minCount} MIN, {btnCount} BTN) above fallback ({fallbackReward} scrip).");
            if (nonTimedFallbackPool.Count > 1)
                Log($"Non-timed fallback pool: {string.Join(", ", nonTimedFallbackPool.Select(c => $"{c.ItemName} [{c.GatherType}]"))} ({bestNonTimedFallback?.ScripReward} scrip) — will alternate randomly.");
            else if (bestNonTimedFallback != null)
                Log($"Non-timed fallback: {bestNonTimedFallback.ItemName} [{bestNonTimedFallback.GatherType}] ({bestNonTimedFallback.ScripReward} scrip).");

            // Log rotation pool spawn data for debugging
            foreach (var item in rotationPool)
                Log($"  Pool: {item.ItemName} [{item.GatherType}] {item.ScripReward} scrip, spawns ET {string.Join("/", item.SpawnHours)}, dur {item.SpawnDurationHours}h");

            // If a timed node window is active with enough time, start directly on it
            // to avoid wasting window time on non-timed nodes first.
            var bestTimed = PickBestActiveTimedNode();
            var initialTarget = bestTimed ?? PickRandomFallback();
            if (initialTarget == null)
            {
                // No active timed or non-timed fallback — try any timed as last resort
                initialTarget = rotationPool.FirstOrDefault();
            }
            if (initialTarget == null)
            {
                Log("No eligible collectables found for auto-rotation.");
                TransitionTo(SessionState.Error, "No eligible collectables found.");
                return;
            }

            SelectedCollectable = initialTarget;
            // Prevent immediate rotation switch on the first tick — give the fallback
            // time to settle before considering timed node switches.
            lastRotationSwitchTime = DateTime.UtcNow;
            Log($"Starting scrip farm (auto-rotation): {initialTarget.ItemName} [{initialTarget.GatherType}] for {ScripType.DisplayName()} scrips. Goal: {Goal}");
        }
        else
        {
            // Single-target mode (existing behavior)
            if (collectable.IsTimedNode && collectable.IsGatherable && resolver != null)
            {
                var allCollectables = resolver.GetCollectables(ScripType);
                FallbackCollectable = allCollectables
                    .Where(c => c.IsGatherable && !c.IsTimedNode && c.GatherLevel <= level)
                    .OrderByDescending(c => c.ScripReward)
                    .ThenByDescending(c => c.GatherLevel)
                    .FirstOrDefault();

                if (FallbackCollectable != null)
                    Log($"Timed node detected. Fallback: {FallbackCollectable.ItemName} ({FallbackCollectable.ScripReward} scrip) while waiting.");
                else
                    Log("Timed node detected but no non-timed fallback available — will wait for spawn windows.");
            }

            Log($"Starting scrip farm: {collectable.ItemName} for {ScripType.DisplayName()} scrips. Goal: {Goal}");
        }

        TransitionTo(SessionState.Initializing, "Initializing...");
    }

    public void Stop()
    {
        if (!IsActive) return;
        Log("Session stopped by user.");
        Cleanup();
        TransitionTo(SessionState.Idle, "Stopped.");
    }

    public void Pause()
    {
        if (State is SessionState.Idle or SessionState.Completed or SessionState.Error or SessionState.Paused) return;
        Log("Session paused.");
        TransitionTo(SessionState.Paused, "Paused by user.");
    }

    public void Resume()
    {
        if (State != SessionState.Paused) return;
        // Reset gathering so it re-initializes GBR (re-injects list, force-resets, etc.)
        gatheringStarted = false;
        craftingStarted = false;
        Log("Session resumed.");
        TransitionTo(SessionState.GatheringMaterials, "Resuming...");
    }

    public void Update()
    {
        if (!IsActive) return;
        if (State == SessionState.Paused) return;

        var now = DateTime.UtcNow;
        if ((now - lastUpdate).TotalSeconds < UpdateIntervalSec) return;
        lastUpdate = now;

        try
        {
            switch (State)
            {
                case SessionState.Initializing:
                    TickInitializing();
                    break;
                case SessionState.GatheringMaterials:
                    TickGathering();
                    break;
                case SessionState.Crafting:
                    TickCrafting();
                    break;
                case SessionState.WaitingForTurnIn:
                    TickWaitingForTurnIn();
                    break;
                case SessionState.CheckingGoal:
                    TickCheckingGoal();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            TransitionTo(SessionState.Error, $"Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (IsActive) Cleanup();
    }

    // --- Recipe info for UI ---

    /// <summary>
    /// Returns the recipe ingredients with live inventory counts for the currently selected
    /// crafter collectable, or null if not a crafter collectable.
    /// </summary>
    public List<(string Name, int Needed, int Owned)>? GetRecipeIngredients()
    {
        if (SelectedCollectable == null || !SelectedCollectable.IsCraftable) return null;

        var recipe = recipeResolver.FindRecipeById(SelectedCollectable.RecipeId);
        if (recipe == null) return null;

        var inv = Expedition.Instance.InventoryManager;
        var result = new List<(string Name, int Needed, int Owned)>();
        foreach (var ing in recipe.Ingredients)
        {
            var owned = inv.GetItemCount(ing.ItemId);
            var needed = ing.QuantityNeeded * EffectiveBatchSize;
            result.Add((ing.ItemName, needed, owned));
        }
        return result;
    }

    // --- Session stats ---

    public int CurrentBalance => ScripType.GetBalance();
    public int ScripsEarned => Math.Max(0, CurrentBalance - StartingBalance);

    public double ScripsPerHour
    {
        get
        {
            if (StartTime == null) return 0;
            var hours = (DateTime.UtcNow - StartTime.Value).TotalHours;
            return hours > 0.01 ? ScripsEarned / hours : 0;
        }
    }

    public string GetDurationString()
    {
        if (StartTime == null) return "--";
        var elapsed = DateTime.UtcNow - StartTime.Value;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m"
            : $"{elapsed.Minutes}m {elapsed.Seconds:D2}s";
    }

    public string GetEtaString()
    {
        if (ScripsPerHour <= 0 || CurrentBalance >= Goal) return "--";
        var remaining = Goal - CurrentBalance;
        var hoursLeft = remaining / ScripsPerHour;
        var eta = TimeSpan.FromHours(hoursLeft);
        return eta.TotalHours >= 1
            ? $"{(int)eta.TotalHours}h {eta.Minutes:D2}m"
            : $"{eta.Minutes}m";
    }

    // --- State machine ticks ---

    private void TickInitializing()
    {
        // Validate GBR is available
        if (!ipc.GatherBuddy.IsAvailable)
        {
            TransitionTo(SessionState.Error, "GatherBuddy Reborn is not available.");
            return;
        }

        // For crafter collectables, validate Artisan
        if (SelectedCollectable!.IsCraftable && !ipc.Artisan.IsAvailable)
        {
            TransitionTo(SessionState.Error, "Artisan is not available (required for crafter collectables).");
            return;
        }

        // Initialize CollectableManager reflection
        if (!InitializeCollectableManagerReflection())
        {
            Log("Warning: CollectableManager reflection failed — turn-in will require manual intervention.");
        }

        // Check if we already hit the goal
        if (CurrentBalance >= Goal)
        {
            Log($"Goal already met! Balance: {CurrentBalance} / {Goal}");
            TransitionTo(SessionState.Completed, "Goal already met!");
            return;
        }

        Log($"Initialized. Starting balance: {StartingBalance}. Target: {Goal}.");
        TransitionTo(SessionState.GatheringMaterials, "Starting gathering...");
    }

    private void TickGathering()
    {
        if (!gatheringStarted)
        {
            gatheringStarted = true;
            lastKnownGatherCount = 0;
            lastGatherProgressTime = DateTime.UtcNow;
            StartGathering();
            return;
        }

        if (SelectedCollectable!.IsGatherable)
        {
            // Smart timed-node switching: check if we should swap between primary and fallback
            CheckTimedNodeSwitch();

            // Monitor inventory count: in auto-rotation, count ALL collectable items across
            // the rotation pool (since we switch targets mid-batch). In single-target mode,
            // count only the current target.
            int currentCount;
            if (IsAutoRotation)
                currentCount = GetRotationInventoryCount();
            else
            {
                var targetId = currentGatherTarget?.ItemId ?? SelectedCollectable.ItemId;
                currentCount = GetCollectableInventoryCount(targetId);
            }

            // Guard against baseline drift: if current count dropped below baseline
            // (user manually turned in items, discarded, etc.), re-anchor baseline
            if (currentCount < baselineInventoryCount)
            {
                DalamudApi.Log.Information(
                    $"[ScripFarm] Baseline drift: count={currentCount} < baseline={baselineInventoryCount}. Re-anchoring.");
                baselineInventoryCount = currentCount;
            }

            var gathered = currentCount - baselineInventoryCount;

            // Track progress for stall detection
            if (gathered > lastKnownGatherCount)
            {
                lastKnownGatherCount = gathered;
                lastGatherProgressTime = DateTime.UtcNow;
            }

            var effectiveBatch = EffectiveBatchSize;
            if (gathered >= effectiveBatch)
            {
                // Stop GBR auto-gather
                ipc.GatherBuddy.SetAutoGatherEnabled(false);
                gatheringStarted = false;
                var targetName = currentGatherTarget?.ItemName ?? SelectedCollectable.ItemName;

                // Record that we farmed this timed node so we don't re-farm the same window
                if (currentGatherTarget is { IsTimedNode: true })
                    lastFarmedTime[currentGatherTarget.ItemId] = DateTime.UtcNow;

                Log($"Gathered {gathered}x {targetName}.");
                TransitionTo(SessionState.WaitingForTurnIn, "Starting turn-in...");
                return;
            }

            // Stall detection: if no progress for 2 minutes, re-inject and reset GBR
            var stallDuration = (DateTime.UtcNow - lastGatherProgressTime).TotalSeconds;
            if (stallDuration > GatherStallTimeoutSec)
            {
                Log($"Gathering stalled at {gathered}/{effectiveBatch} for {stallDuration:F0}s. Re-initializing GBR...");
                ipc.GatherBuddy.SetAutoGatherEnabled(false);
                gatheringStarted = false; // Will re-enter StartGathering on next tick
                return;
            }

            var gatherName = currentGatherTarget?.ItemName ?? SelectedCollectable.ItemName;
            var suffix = IsAutoRotation
                ? $" [{currentGatherTarget?.GatherType}]"
                : (IsGatheringFallback ? " (fallback)" : "");
            StatusMessage = $"Gathering: {gathered}/{effectiveBatch} {gatherName}{suffix}...";
        }
        else
        {
            // For crafter collectables, use the gathering orchestrator for ingredients
            // Must tick the orchestrator since WorkflowEngine is idle
            gatheringOrchestrator.Update(Expedition.Instance.InventoryManager);

            var gatherState = gatheringOrchestrator.State;

            if (gatherState is GatheringOrchestratorState.Completed or GatheringOrchestratorState.Idle)
            {
                gatheringStarted = false;
                Log("Ingredient gathering complete.");
                TransitionTo(SessionState.Crafting, "Starting crafting...");
                return;
            }

            if (gatherState == GatheringOrchestratorState.Error)
            {
                gatheringStarted = false;
                TransitionTo(SessionState.Error, $"Gathering failed: {gatheringOrchestrator.StatusMessage}");
                return;
            }

            StatusMessage = $"Gathering ingredients: {gatheringOrchestrator.StatusMessage}";
        }
    }

    /// <summary>
    /// Checks if we should switch gather targets.
    /// In auto-rotation mode: chases the highest-reward active timed node across MIN/BTN.
    /// In single-target mode: switches between primary timed collectable and non-timed fallback.
    /// </summary>
    private void CheckTimedNodeSwitch()
    {
        if (IsAutoRotation)
        {
            CheckRotationSwitch();
            return;
        }

        // Single-target mode (existing behavior)
        if (SelectedCollectable == null || !SelectedCollectable.IsTimedNode || FallbackCollectable == null)
            return;

        var timedWindowActive = IsTimedWindowActive(SelectedCollectable);
        var shouldGatherPrimary = timedWindowActive;
        var currentlyGatheringPrimary = currentGatherTarget?.ItemId == SelectedCollectable.ItemId;

        // Don't switch while gathering window is open or mid-teleport
        if (IsPlayerInGatheringWindow()) return;

        // Cooldown: don't switch again too soon
        if ((DateTime.UtcNow - lastRotationSwitchTime).TotalSeconds < RotationSwitchCooldownSec) return;

        if (shouldGatherPrimary && !currentlyGatheringPrimary)
        {
            lastRotationSwitchTime = DateTime.UtcNow;
            Log($"Timed window active! Switching to {SelectedCollectable.ItemName}.");
            ipc.GatherBuddy.SetAutoGatherEnabled(false);
            SwitchGatherTarget(SelectedCollectable);
        }
        else if (!shouldGatherPrimary && currentlyGatheringPrimary)
        {
            lastRotationSwitchTime = DateTime.UtcNow;
            var nextSpawn = GetSecondsUntilNextSpawn(SelectedCollectable);
            Log($"Timed window closed. Switching to {FallbackCollectable.ItemName} (next window: {EorzeanTime.FormatRealDuration(nextSpawn)}).");
            ipc.GatherBuddy.SetAutoGatherEnabled(false);
            SwitchGatherTarget(FallbackCollectable);
        }
    }

    /// <summary>
    /// Auto-rotation: picks the highest-reward active timed node, or non-timed fallback.
    /// Switches target if the best option changed. Waits if player is mid-gather.
    /// </summary>
    private void CheckRotationSwitch()
    {
        var bestActive = PickBestActiveTimedNode();
        var chosenTarget = bestActive ?? PickRandomFallback();

        if (chosenTarget == null || currentGatherTarget == null) return;
        if (chosenTarget.ItemId == currentGatherTarget.ItemId) return;

        // Don't switch while gathering window is open or mid-teleport
        if (IsPlayerInGatheringWindow()) return;

        // Cooldown: don't switch again too soon — gives GBR time to finish the current
        // node interaction cycle (approach → open node → gather all picks → close node)
        if ((DateTime.UtcNow - lastRotationSwitchTime).TotalSeconds < RotationSwitchCooldownSec) return;

        // Target changed — switch
        lastRotationSwitchTime = DateTime.UtcNow;
        var classTag = $"[{chosenTarget.GatherType}]";
        if (bestActive != null)
            Log($"Switching to {chosenTarget.ItemName} {classTag} ({chosenTarget.ScripReward} scrip) — timed window active.");
        else
            Log($"No timed windows active. Switching to {chosenTarget.ItemName} {classTag} ({chosenTarget.ScripReward} scrip).");

        ipc.GatherBuddy.SetAutoGatherEnabled(false);
        SwitchGatherTarget(chosenTarget);
    }

    // Minimum real-world seconds remaining in a timed window before we bother switching.
    // 120 real seconds (~2 min) = ~0.7 ET hours. Allows for teleport (~15s) + approach (~15s)
    // + several gathering cycles. Standard unspoiled windows are 2 ET hours = 350s total,
    // so this threshold lets us use ~65% of the window.
    private const double MinWindowSecondsToSwitch = 120.0;

    /// <summary>
    /// Picks a random non-timed fallback from the pool (alternates between MIN/BTN).
    /// Falls back to the single bestNonTimedFallback if pool is empty.
    /// </summary>
    private ScripCollectableInfo? PickRandomFallback()
    {
        if (nonTimedFallbackPool.Count > 1)
            return nonTimedFallbackPool[rng.Next(nonTimedFallbackPool.Count)];
        return bestNonTimedFallback;
    }

    /// <summary>
    /// Returns the highest-reward timed collectable that currently has an active ET window
    /// with enough remaining time to be worth switching to, or null.
    /// Skips nodes whose current window was already farmed (batch completed recently).
    /// </summary>
    private ScripCollectableInfo? PickBestActiveTimedNode()
    {
        for (var i = 0; i < rotationPool.Count; i++)
        {
            var node = rotationPool[i];
            var remaining = GetRemainingWindowSeconds(node);
            if (remaining <= MinWindowSecondsToSwitch) continue;

            // Skip if we already farmed this node during its current window.
            // A window lasts ~350 real seconds max, so if we farmed it less than
            // that ago and the window is still active, it's the same spawn.
            if (lastFarmedTime.TryGetValue(node.ItemId, out var farmedAt))
            {
                var secsSinceFarmed = (DateTime.UtcNow - farmedAt).TotalSeconds;
                if (secsSinceFarmed < 400) // generous buffer over 350s window
                    continue;
            }

            return node; // Pool is sorted by reward desc
        }
        return null;
    }

    /// <summary>
    /// Returns how many real-world seconds remain in the currently active window for a timed node,
    /// or 0 if no window is active.
    /// </summary>
    private static double GetRemainingWindowSeconds(ScripCollectableInfo collectable)
    {
        if (!collectable.IsTimedNode || collectable.SpawnHours.Length == 0) return 0;

        var currentHour = EorzeanTime.CurrentHour;
        var currentMinute = EorzeanTime.CurrentMinute;

        for (var i = 0; i < collectable.SpawnHours.Length; i++)
        {
            var start = collectable.SpawnHours[i];
            var duration = collectable.SpawnDurationHours;
            if (!EorzeanTime.IsWithinWindow(start, duration)) continue;

            // Calculate end hour and how many ET hours+minutes remain
            var endHour = (start + duration) % 24;
            int hoursLeft;
            if (endHour > currentHour)
                hoursLeft = endHour - currentHour;
            else if (endHour < currentHour)
                hoursLeft = 24 - currentHour + endHour; // wraps midnight
            else
                hoursLeft = currentMinute == 0 ? 0 : 24; // exactly on end hour

            // Subtract current minute progress within the hour
            var minuteFraction = currentMinute / 60.0;
            var etHoursRemaining = hoursLeft - minuteFraction;
            if (etHoursRemaining <= 0) continue;

            // Convert ET hours to real seconds (1 ET hour = 175 real seconds)
            return etHoursRemaining * 175.0;
        }

        return 0;
    }

    private void SwitchGatherTarget(ScripCollectableInfo target)
    {
        currentGatherTarget = target;
        // Baseline must match how TickGathering counts: rotation total for auto-rotation,
        // single-item count otherwise. Mismatch causes inflated/wrong gathered counts.
        baselineInventoryCount = IsAutoRotation
            ? GetRotationInventoryCount()
            : GetCollectableInventoryCount(target.ItemId);

        // GBR gather list always uses the single-item count for its target
        var singleItemCount = GetCollectableInventoryCount(target.ItemId);
        var targetCount = singleItemCount + EffectiveBatchSize;
        var success = ipc.GatherBuddyLists.SetGatherList(
            new[] { (target.ItemId, (uint)targetCount) });

        if (!success)
        {
            Log($"Failed to switch gather target to {target.ItemName}.");
            return;
        }

        // Force-reset GBR to pick up the new list
        if (ipc.GbrStateTracker.IsInitialized)
            ipc.GbrStateTracker.ForceReset(ipc.GatherBuddy);

        ipc.GatherBuddy.SetAutoGatherEnabled(true);
    }

    private static bool IsTimedWindowActive(ScripCollectableInfo collectable)
    {
        if (!collectable.IsTimedNode || collectable.SpawnHours.Length == 0) return true;
        for (var i = 0; i < collectable.SpawnHours.Length; i++)
        {
            if (EorzeanTime.IsWithinWindow(collectable.SpawnHours[i], collectable.SpawnDurationHours))
                return true;
        }
        return false;
    }

    private static double GetSecondsUntilNextSpawn(ScripCollectableInfo collectable)
    {
        if (collectable.SpawnHours.Length == 0) return 0;
        var min = double.MaxValue;
        for (var i = 0; i < collectable.SpawnHours.Length; i++)
        {
            var s = EorzeanTime.SecondsUntilEorzeanHour(collectable.SpawnHours[i]);
            if (s < min) min = s;
        }
        return min;
    }

    private void TickCrafting()
    {
        // Pre-craft teleport phase (if enabled)
        if (!craftingStarted && Expedition.Config.ScripTeleportBeforeCrafting)
        {
            if (!preCraftTeleportSent)
            {
                // Don't teleport if player is busy
                if (IsPlayerBusyForTurnIn())
                {
                    StatusMessage = "Waiting to teleport for crafting...";
                    return;
                }

                var (destName, cmd) = GetTeleportDestination();
                Log($"Teleporting to {destName} before crafting...");
                StatusMessage = $"Teleporting to {destName}...";
                try
                {
                    IPC.ChatIpc.SendCommand(cmd);
                }
                catch (Exception ex)
                {
                    Log($"Teleport failed: {ex.Message}. Crafting at current location.");
                    preCraftTeleportSent = true;
                    preCraftTeleportArrived = true;
                    return;
                }
                preCraftTeleportSent = true;
                preCraftTeleportArrived = false;
                preCraftTeleportSentTime = DateTime.UtcNow;
                return;
            }

            if (!preCraftTeleportArrived)
            {
                var cond = DalamudApi.Condition;
                var isTravelling = cond[ConditionFlag.BetweenAreas]
                    || cond[ConditionFlag.BetweenAreas51]
                    || cond[ConditionFlag.Occupied]
                    || cond[ConditionFlag.Casting];

                var elapsed = (DateTime.UtcNow - preCraftTeleportSentTime).TotalSeconds;

                if (isTravelling)
                {
                    StatusMessage = $"Teleporting... ({elapsed:F0}s)";
                    return;
                }

                // Brief grace period for Lifestream to initiate
                if (elapsed < 5.0)
                {
                    StatusMessage = $"Waiting for teleport... ({elapsed:F0}s)";
                    return;
                }

                if (elapsed > TeleportTimeoutSec)
                    Log("Teleport timed out. Crafting at current location.");
                else
                    Log("Arrived at crafting destination.");

                preCraftTeleportArrived = true;
                return;
            }
        }

        if (!craftingStarted)
        {
            craftingStarted = true;
            StartCrafting();
            return;
        }

        // Must tick the orchestrator since WorkflowEngine is idle
        craftingOrchestrator.Update(Expedition.Instance.InventoryManager);

        // Poll crafting orchestrator state
        var craftState = craftingOrchestrator.State;

        if (craftState is CraftingOrchestratorState.Completed or CraftingOrchestratorState.Idle)
        {
            craftingStarted = false;
            CloseCraftingWindows();
            Log("Crafting complete.");
            TransitionTo(SessionState.WaitingForTurnIn, "Starting turn-in...");
            return;
        }

        if (craftState == CraftingOrchestratorState.Error)
        {
            craftingStarted = false;
            TransitionTo(SessionState.Error, $"Crafting failed: {craftingOrchestrator.StatusMessage}");
            return;
        }

        StatusMessage = $"Crafting: {craftingOrchestrator.StatusMessage}";
    }

    private static (string destName, string cmd) GetTeleportDestination()
    {
        return Expedition.Config.ScripTeleportDestination switch
        {
            1 => ("FC Estate", "/li fc"),
            2 => ("Private Estate", "/li home"),
            3 => ("Apartment", "/li apartment"),
            _ => ("Eulmore", "/li Eulmore"),
        };
    }

    private void TickWaitingForTurnIn()
    {
        if (!turnInStarted)
        {
            // Wait for the player to finish any active gathering/crafting before triggering turn-in.
            // GBR's CollectableManager can't teleport while the player is at a node or crafting bench.
            if (IsPlayerBusyForTurnIn())
            {
                StatusMessage = "Waiting for current gather to finish...";
                return;
            }

            // Brief delay after gathering finishes to let GBR settle
            if (turnInReadyTime == DateTime.MinValue)
            {
                turnInReadyTime = DateTime.UtcNow.AddSeconds(TurnInDelaySec);
                StatusMessage = "Preparing for turn-in...";
                return;
            }

            if (DateTime.UtcNow < turnInReadyTime)
                return;

            turnInStarted = true;
            turnInReadyTime = DateTime.MinValue;
            turnInStartTime = DateTime.UtcNow;

            if (!TriggerCollectableTurnIn())
            {
                Log("Could not trigger automatic turn-in. Pausing for manual turn-in.");
                TransitionTo(SessionState.Paused, "Manual turn-in required. Resume after turning in collectables.");
                return;
            }

            StatusMessage = "Waiting for GBR turn-in to complete...";
            return;
        }

        // Poll CollectableManager.IsRunning
        var isRunning = GetCollectableManagerIsRunning();
        if (!isRunning)
        {
            turnInStarted = false;
            Log("Turn-in complete.");
            TransitionTo(SessionState.CheckingGoal, "Checking scrip balance...");
            return;
        }

        // Timeout check
        if ((DateTime.UtcNow - turnInStartTime).TotalSeconds > TurnInTimeoutSec)
        {
            turnInStarted = false;
            Log("Turn-in timed out. Pausing for manual intervention.");
            StopCollectableManager();
            TransitionTo(SessionState.Paused, "Turn-in timed out. Resume after manual turn-in.");
            return;
        }

        StatusMessage = "GBR turning in collectables...";
    }

    private void TickCheckingGoal()
    {
        var balance = CurrentBalance;
        var earned = ScripsEarned;
        LoopCount++;

        Log($"Loop {LoopCount} done. Balance: {balance} / {Goal} (+{earned} total earned)");

        if (balance >= Goal)
        {
            Log($"Goal reached! Final balance: {balance}");
            TransitionTo(SessionState.Completed, $"Goal reached! {balance} / {Goal} scrips.");
            return;
        }

        // Loop back to gathering
        gatheringStarted = false;
        craftingStarted = false;
        turnInStarted = false;
        turnInReadyTime = DateTime.MinValue;
        preCraftTeleportSent = false;
        preCraftTeleportArrived = false;
        TransitionTo(SessionState.GatheringMaterials, "Starting next loop...");
    }

    // --- Gathering setup ---

    private void StartGathering()
    {
        var collectable = SelectedCollectable!;

        if (collectable.IsGatherable)
        {
            ScripCollectableInfo initialTarget;

            if (IsAutoRotation)
            {
                // Always start each batch with the non-timed fallback. The rotation check
                // running every tick will switch to a timed node once we confirm the window
                // is solidly active with enough remaining time. This prevents GBR getting
                // stuck on "No available items to gather" from spawn data inaccuracies.
                initialTarget = bestNonTimedFallback ?? collectable;
                var classTag = $"[{initialTarget.GatherType}]";
                Log($"Auto-rotation starting batch: {initialTarget.ItemName} {classTag} ({initialTarget.ScripReward} scrip).");
            }
            else if (collectable.IsTimedNode && FallbackCollectable != null && !IsTimedWindowActive(collectable))
            {
                // Single-target timed-node logic
                initialTarget = FallbackCollectable;
                var nextSpawn = GetSecondsUntilNextSpawn(collectable);
                Log($"Timed node not active. Starting with {initialTarget.ItemName} (next window: {EorzeanTime.FormatRealDuration(nextSpawn)}).");
            }
            else
            {
                initialTarget = collectable;
            }

            currentGatherTarget = initialTarget;
            baselineInventoryCount = IsAutoRotation
                ? GetRotationInventoryCount()
                : GetCollectableInventoryCount(initialTarget.ItemId);

            // Use GBR list manager to set up the gather list
            var effectiveBatch = EffectiveBatchSize;
            var targetCount = GetCollectableInventoryCount(initialTarget.ItemId) + effectiveBatch;
            var success = ipc.GatherBuddyLists.SetGatherList(
                new[] { (initialTarget.ItemId, (uint)targetCount) });

            if (!success)
            {
                TransitionTo(SessionState.Error, $"Failed to inject gather list for {initialTarget.ItemName}.");
                return;
            }

            // Apply gathering skill preset (cordials, yield skills, etc.)
            if (Expedition.Config.AutoApplyGatheringSkills)
            {
                ipc.GatherBuddyLists.ApplyGatheringSkillPreset(
                    enableCordials: Expedition.Config.UseCordials);
            }

            // Initialize GBR state tracker if needed
            if (!ipc.GbrStateTracker.IsInitialized && ipc.GatherBuddyLists.GbrPluginInstance != null)
                ipc.GbrStateTracker.Initialize(ipc.GatherBuddyLists.GbrPluginInstance);

            // Force-reset GBR's internal state to clear stale task queues from previous sessions
            if (ipc.GbrStateTracker.IsInitialized)
            {
                DalamudApi.Log.Information("[ScripFarm] Pre-start GBR force-reset.");
                ipc.GbrStateTracker.ForceReset(ipc.GatherBuddy);
            }

            ipc.GatherBuddy.SetAutoGatherEnabled(true);
            Log($"Gathering {effectiveBatch}x {initialTarget.ItemName} (baseline: {baselineInventoryCount})...");
        }
        else if (collectable.IsCraftable)
        {
            // Crafter collectable: resolve recipe and gather ingredients
            var recipe = recipeResolver.FindRecipeById(collectable.RecipeId);
            if (recipe == null)
            {
                TransitionTo(SessionState.Error, $"Could not find recipe for {collectable.ItemName}.");
                return;
            }

            // Resolve with inventory awareness so we only gather what's actually missing.
            // If all materials are already in inventory, skip straight to crafting.
            var craftBatch = EffectiveBatchSize;
            var inv = Expedition.Instance.InventoryManager;
            var resolved = recipeResolver.Resolve(recipe, craftBatch,
                inventoryLookup: itemId => inv.GetItemCount(itemId));

            if (resolved.GatherList.All(m => m.QuantityRemaining <= 0))
            {
                Log("All materials already in inventory — skipping to crafting.");
                gatheringStarted = false;
                TransitionTo(SessionState.Crafting, "Starting crafting...");
                return;
            }

            gatheringOrchestrator.BuildQueue(resolved);
            gatheringOrchestrator.Start();
            Log($"Gathering materials for {craftBatch}x {collectable.ItemName}...");
        }
        else
        {
            TransitionTo(SessionState.Error, $"Collectable {collectable.ItemName} is neither craftable nor gatherable.");
        }
    }

    // --- Crafting setup ---

    private void StartCrafting()
    {
        var collectable = SelectedCollectable!;
        if (!collectable.IsCraftable)
        {
            craftingStarted = false;
            TransitionTo(SessionState.WaitingForTurnIn, "Not a crafter collectable — skipping craft.");
            return;
        }

        // Resolve recipe to get the craft order for the orchestrator
        var recipe = recipeResolver.FindRecipeById(collectable.RecipeId);
        if (recipe == null)
        {
            TransitionTo(SessionState.Error, $"Could not find recipe for {collectable.ItemName}.");
            return;
        }

        // Use inventory-aware resolve to skip intermediate crafts that are already in inventory
        // (e.g., Turali Corn Oil for Rarefied Stuffed Peppers). Exclude the final collectable
        // from deduction so we always craft EffectiveBatchSize of the target item.
        var craftBatch = EffectiveBatchSize;
        var inv = Expedition.Instance.InventoryManager;
        var finalItemId = collectable.ItemId;
        var resolved = recipeResolver.Resolve(recipe, craftBatch,
            inventoryLookup: itemId => itemId == finalItemId ? 0 : inv.GetItemCount(itemId));
        craftingOrchestrator.BuildQueue(resolved,
            preferredSolver: Expedition.Config.PreferredSolver,
            collectablePreferredSolver: Expedition.Config.CollectablePreferredSolver);
        craftingOrchestrator.Start();
        Log($"Crafting {craftBatch}x {collectable.ItemName}...");
    }

    // --- CollectableManager reflection ---

    private bool InitializeCollectableManagerReflection()
    {
        if (collectableManagerInitialized) return collectableManagerInstance != null;
        collectableManagerInitialized = true;

        try
        {
            // Get GBR plugin instance from our existing list manager
            var gbrInstance = ipc.GatherBuddyLists.GbrPluginInstance;
            if (gbrInstance == null)
            {
                // Try to initialize list manager first
                ipc.GatherBuddyLists.Initialize();
                gbrInstance = ipc.GatherBuddyLists.GbrPluginInstance;
            }

            if (gbrInstance == null)
            {
                DalamudApi.Log.Warning("[ScripFarm] Could not get GBR plugin instance for CollectableManager.");
                return false;
            }

            // CollectableManager is a static property on the GatherBuddy class
            var gbrAssembly = gbrInstance.GetType().Assembly;
            var gatherBuddyClass = gbrAssembly.GetTypes()
                .FirstOrDefault(t => t.Name == "GatherBuddy" && !t.IsInterface);

            if (gatherBuddyClass == null)
            {
                DalamudApi.Log.Warning("[ScripFarm] Could not find GatherBuddy class in GBR assembly.");
                return false;
            }

            var cmProp = gatherBuddyClass.GetProperty("CollectableManager", AllFlags);
            if (cmProp == null)
            {
                DalamudApi.Log.Warning("[ScripFarm] Could not find CollectableManager property.");
                return false;
            }

            collectableManagerInstance = cmProp.GetValue(null);
            if (collectableManagerInstance == null)
            {
                DalamudApi.Log.Warning("[ScripFarm] CollectableManager instance is null.");
                return false;
            }

            var cmType = collectableManagerInstance.GetType();
            isRunningProp = cmType.GetProperty("IsRunning", AllFlags);
            startMethod = cmType.GetMethod("Start", AllFlags, null, Type.EmptyTypes, null);
            stopMethod = cmType.GetMethod("Stop", AllFlags, null, Type.EmptyTypes, null);
            hasCollectablesMethod = cmType.GetMethod("HasCollectables", AllFlags);

            if (isRunningProp == null || startMethod == null)
            {
                DalamudApi.Log.Warning("[ScripFarm] Could not find IsRunning/Start on CollectableManager.");
                collectableManagerInstance = null;
                return false;
            }

            // Find Config.CollectableConfig.AutoTurnInCollectables to enable it
            var configProp = gatherBuddyClass.GetProperty("Config", AllFlags);
            if (configProp != null)
            {
                var configInstance = configProp.GetValue(null);
                if (configInstance != null)
                {
                    var collectableConfigProp = configInstance.GetType().GetProperty("CollectableConfig", AllFlags);
                    if (collectableConfigProp != null)
                    {
                        var collectableConfig = collectableConfigProp.GetValue(configInstance);
                        if (collectableConfig != null)
                        {
                            autoTurnInProp = collectableConfig.GetType()
                                .GetProperty("AutoTurnInCollectables", AllFlags);
                            if (autoTurnInProp != null)
                            {
                                DalamudApi.Log.Information(
                                    $"[ScripFarm] Found AutoTurnInCollectables (current: {autoTurnInProp.GetValue(collectableConfig)})");
                            }
                        }
                    }
                }
            }

            DalamudApi.Log.Information("[ScripFarm] CollectableManager reflection initialized successfully.");
            return true;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[ScripFarm] CollectableManager reflection failed.");
            return false;
        }
    }

    private bool TriggerCollectableTurnIn()
    {
        if (collectableManagerInstance == null || startMethod == null)
            return false;

        try
        {
            // Enable AutoTurnInCollectables in GBR config (defaults to false)
            EnableAutoTurnIn();

            // Check if GBR sees collectables in inventory
            if (hasCollectablesMethod != null)
            {
                var hasItems = false;
                try
                {
                    var result = hasCollectablesMethod.Invoke(collectableManagerInstance, null);
                    hasItems = result is true;
                }
                catch { }

                if (!hasItems)
                {
                    Log("Warning: GBR CollectableManager reports no collectables in inventory.");
                    // Log inventory state for debugging
                    var targetId = currentGatherTarget?.ItemId ?? SelectedCollectable?.ItemId ?? 0;
                    if (targetId > 0)
                    {
                        var count = GetCollectableInventoryCount(targetId);
                        Log($"  Inventory check: {count} collectable items with ID {targetId}");
                    }
                }
            }

            startMethod.Invoke(collectableManagerInstance, null);

            // Verify it actually started
            var isRunning = GetCollectableManagerIsRunning();
            DalamudApi.Log.Information($"[ScripFarm] Triggered CollectableManager.Start() — IsRunning: {isRunning}");

            if (!isRunning)
            {
                Log("Warning: CollectableManager.Start() was called but IsRunning is false. " +
                    "GBR may not have found collectables to turn in.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[ScripFarm] Failed to invoke CollectableManager.Start()");
            return false;
        }
    }

    /// <summary>
    /// Enables GBR's AutoTurnInCollectables config flag via reflection.
    /// This flag defaults to false and must be enabled for CollectableManager.Start() to work.
    /// </summary>
    private void EnableAutoTurnIn()
    {
        if (autoTurnInProp == null) return;

        try
        {
            // Re-navigate to the config instance each time (in case it was recreated)
            var gbrInstance = ipc.GatherBuddyLists.GbrPluginInstance;
            if (gbrInstance == null) return;

            var gatherBuddyClass = gbrInstance.GetType().Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "GatherBuddy" && !t.IsInterface);
            if (gatherBuddyClass == null) return;

            var configProp = gatherBuddyClass.GetProperty("Config", AllFlags);
            var configInstance = configProp?.GetValue(null);
            if (configInstance == null) return;

            var collectableConfigProp = configInstance.GetType().GetProperty("CollectableConfig", AllFlags);
            var collectableConfig = collectableConfigProp?.GetValue(configInstance);
            if (collectableConfig == null) return;

            var currentValue = autoTurnInProp.GetValue(collectableConfig);
            if (currentValue is not true)
            {
                autoTurnInProp.SetValue(collectableConfig, true);
                DalamudApi.Log.Information("[ScripFarm] Enabled AutoTurnInCollectables in GBR config.");
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "[ScripFarm] Failed to enable AutoTurnInCollectables.");
        }
    }

    private bool GetCollectableManagerIsRunning()
    {
        if (collectableManagerInstance == null || isRunningProp == null)
            return false;

        try
        {
            return (bool)(isRunningProp.GetValue(collectableManagerInstance) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private void StopCollectableManager()
    {
        if (collectableManagerInstance == null || stopMethod == null) return;

        try
        {
            stopMethod.Invoke(collectableManagerInstance, null);
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "[ScripFarm] Failed to stop CollectableManager.");
        }
    }

    // --- Helpers ---

    private static unsafe int GetCollectableInventoryCount(uint itemId)
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (manager == null) return 0;
        // Count collectable items specifically (they stack separately as individual slots)
        var count = 0;
        var invTypes = new[]
        {
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3,
            FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4,
        };
        foreach (var invType in invTypes)
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId) continue;
                if ((slot->Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.Collectable) != 0)
                    count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Counts total collectable items in inventory across all items in the rotation pool + fallback.
    /// Used for batch tracking in auto-rotation mode.
    /// </summary>
    private int GetRotationInventoryCount()
    {
        var total = 0;
        var counted = new HashSet<uint>();

        foreach (var item in rotationPool)
        {
            if (counted.Add(item.ItemId))
                total += GetCollectableInventoryCount(item.ItemId);
        }

        foreach (var fb in nonTimedFallbackPool)
        {
            if (counted.Add(fb.ItemId))
                total += GetCollectableInventoryCount(fb.ItemId);
        }
        if (bestNonTimedFallback != null && counted.Add(bestNonTimedFallback.ItemId))
            total += GetCollectableInventoryCount(bestNonTimedFallback.ItemId);

        return total;
    }

    /// <summary>
    /// Returns info about the next upcoming timed window in the rotation pool.
    /// </summary>
    /// <summary>Status info for a single rotation pool item, for UI display.</summary>
    public record struct RotationNodeStatus(
        string ItemName, GatherType GatherType, int ScripReward,
        string SpawnHours, int DurationHours,
        bool IsActive, double RemainingSeconds, double SecondsUntilNext, bool IsCurrent);

    /// <summary>Returns status info for each item in the rotation pool.</summary>
    public List<RotationNodeStatus> GetRotationPoolStatus()
    {
        var result = new List<RotationNodeStatus>();
        foreach (var item in rotationPool)
        {
            var active = IsTimedWindowActive(item);
            var remaining = active ? GetRemainingWindowSeconds(item) : 0;
            var nextSpawn = active ? 0 : GetSecondsUntilNextSpawn(item);
            var isCurrent = currentGatherTarget?.ItemId == item.ItemId;
            result.Add(new RotationNodeStatus(
                item.ItemName, item.GatherType, item.ScripReward,
                string.Join("/", item.SpawnHours), item.SpawnDurationHours,
                active, remaining, nextSpawn, isCurrent));
        }
        return result;
    }

    public (string ItemName, GatherType GatherType, double SecondsUntil)? GetNextTimedWindow()
    {
        if (rotationPool.Count == 0) return null;

        string? bestName = null;
        var bestType = GatherType.None;
        var bestSeconds = double.MaxValue;

        foreach (var item in rotationPool)
        {
            if (IsTimedWindowActive(item)) continue; // Skip active ones
            var seconds = GetSecondsUntilNextSpawn(item);
            if (seconds < bestSeconds)
            {
                bestSeconds = seconds;
                bestName = item.ItemName;
                bestType = item.GatherType;
            }
        }

        return bestName != null ? (bestName, bestType, bestSeconds) : null;
    }

    private void TransitionTo(SessionState newState, string message)
    {
        DalamudApi.Log.Information($"[ScripFarm] {State} -> {newState}: {message}");
        State = newState;
        StatusMessage = message;
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        activityLog.Add($"[{timestamp}] {message}");
        DalamudApi.Log.Information($"[ScripFarm] {message}");

        // Cap log size
        while (activityLog.Count > 200)
            activityLog.RemoveAt(0);
    }

    /// <summary>
    /// Broad check: player is gathering, in a cutscene, occupied, or zone-loading.
    /// Used for turn-in readiness (don't teleport to NPC while any of these are active).
    /// </summary>
    private static bool IsPlayerGathering()
    {
        var cond = DalamudApi.Condition;
        return cond[ConditionFlag.Gathering]
            || cond[ConditionFlag.ExecutingGatheringAction]
            || cond[ConditionFlag.Occupied]
            || cond[ConditionFlag.Occupied30]
            || cond[ConditionFlag.Occupied38]
            || cond[ConditionFlag.BetweenAreas]
            || cond[ConditionFlag.BetweenAreas51];
    }

    /// <summary>
    /// Closes any open crafting-related game windows (RecipeNote, Synthesis).
    /// Called after crafting completes so the player exits crafting mode for turn-in.
    /// </summary>
    private static unsafe void CloseCraftingWindows()
    {
        var addonNames = new[] { "Synthesis", "SynthesisSimple", "RecipeNote" };
        foreach (var name in addonNames)
        {
            try
            {
                var wrapper = DalamudApi.GameGui.GetAddonByName(name);
                if (!wrapper.IsNull && wrapper.IsVisible)
                {
                    var addon = (AtkUnitBase*)wrapper.Address;
                    addon->Close(true);
                    DalamudApi.Log.Information($"[ScripFarm] Closed {name} addon.");
                }
            }
            catch (Exception ex)
            {
                DalamudApi.Log.Warning($"[ScripFarm] Failed to close {name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Check for turn-in readiness: blocks if player is gathering, crafting, or zone-loading.
    /// Covers both gatherer and crafter scrip workflows.
    /// </summary>
    private static bool IsPlayerBusyForTurnIn()
    {
        var cond = DalamudApi.Condition;
        return cond[ConditionFlag.Gathering]
            || cond[ConditionFlag.ExecutingGatheringAction]
            || cond[ConditionFlag.Crafting]
            || cond[ConditionFlag.PreparingToCraft]
            || cond[ConditionFlag.BetweenAreas]
            || cond[ConditionFlag.BetweenAreas51];
    }

    /// <summary>
    /// Narrow check: player has a gathering node window open or is mid-teleport.
    /// Used for rotation switching — only blocks when the gathering UI is physically open,
    /// NOT when GBR is navigating/searching between nodes.
    /// </summary>
    private static bool IsPlayerInGatheringWindow()
    {
        var cond = DalamudApi.Condition;
        return cond[ConditionFlag.Gathering]
            || cond[ConditionFlag.ExecutingGatheringAction]
            || cond[ConditionFlag.BetweenAreas]
            || cond[ConditionFlag.BetweenAreas51];
    }

    private void Cleanup()
    {
        ipc.GatherBuddy.SetAutoGatherEnabled(false);
        StopCollectableManager();
        turnInStarted = false;
        turnInReadyTime = DateTime.MinValue;
        gatheringStarted = false;
        craftingStarted = false;
    }
}
