using System.Numerics;
using Dalamud.Bindings.ImGui;

using Expedition.Scrip;

namespace Expedition.UI;

/// <summary>
/// Draws the Scrip Farming tab for automated scrip acquisition.
/// Follows the static tab pattern used by FishingTab and CosmicTab.
/// </summary>
public static class ScripTab
{
    // Gold/amber accent for scrip theme
    private static readonly Vector4 ScripAccent = new(0.92f, 0.75f, 0.20f, 1.00f);
    private static readonly Vector4 ScripDim = new(0.60f, 0.50f, 0.15f, 1.00f);

    // Config-backed local state
    private static bool configLoaded;
    private static int scripTypeIndex = 2; // default OrangeCrafter
    private static uint selectedCollectableId;
    private static int goalAmount = 4000;
    private static int batchSize = 10;

    // Cached collectable list for current scrip type
    private static ScripType cachedScripType;
    private static IReadOnlyList<ScripCollectableInfo> cachedCollectables = Array.Empty<ScripCollectableInfo>();
    private static string[] cachedNames = Array.Empty<string>();
    private static int selectedCollectableIndex;

    public static void Draw(Expedition plugin)
    {
        EnsureConfigLoaded();

        var session = plugin.ScripFarmingSession;
        var resolver = plugin.ScripDataResolver;

        DrawHeader(session);
        ImGui.Spacing();
        DrawConfigCard(session, resolver);
        ImGui.Spacing();
        DrawSessionCard(session);
        ImGui.Spacing();
        DrawLogCard(session);
    }

    // ──────────────────────────────────────────────
    // Header Banner
    // ──────────────────────────────────────────────

    private static void DrawHeader(ScripFarmingSession session)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        const float headerH = 48f;

        // Banner background
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + headerH),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.14f, 0.12f, 0.06f, 1.00f)),
            Theme.Rounding);

        // Accent line
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + 2),
            ImGui.ColorConvertFloat4ToU32(ScripAccent),
            Theme.Rounding);

        // Title
        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, pos.Y + 6f),
            ImGui.ColorConvertFloat4ToU32(ScripAccent), "Scrip Farming  —  Automated Collectable Turn-in");

        // Status line
        var scripType = (ScripType)scripTypeIndex;
        var balance = scripType.GetBalance();
        var statusText = session.IsActive
            ? $"Session: {session.GetDurationString()}  |  +{session.ScripsEarned} scrips  |  {session.ScripsPerHour:F0}/hr  |  ETA: {session.GetEtaString()}"
            : $"Idle  |  {scripType.DisplayName()} Balance: {balance}";

        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, pos.Y + 26f),
            ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary), statusText);

        // Start/Stop/Pause buttons
        var btnX = pos.X + avail - 260;
        ImGui.SetCursorScreenPos(new Vector2(btnX, pos.Y + 10f));

        if (session.IsActive)
        {
            if (session.State == ScripFarmingSession.SessionState.Paused)
            {
                if (Theme.PrimaryButton("Resume##scrip", new Vector2(80, 28)))
                    session.Resume();
                ImGui.SameLine(0, 4);
                if (Theme.DangerButton("Stop##scrip", new Vector2(80, 28)))
                    session.Stop();
            }
            else
            {
                if (Theme.SecondaryButton("Pause##scrip", new Vector2(80, 28)))
                    session.Pause();
                ImGui.SameLine(0, 4);
                if (Theme.DangerButton("Stop##scrip", new Vector2(80, 28)))
                    session.Stop();
            }
        }
        else
        {
            if (Theme.PrimaryButton("Start##scrip", new Vector2(120, 28)))
                TryStartSession(Expedition.Instance!);
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + headerH));
        ImGui.Dummy(new Vector2(avail, 0));
    }

    // ──────────────────────────────────────────────
    // Configuration Card
    // ──────────────────────────────────────────────

    private static void DrawConfigCard(ScripFarmingSession session, ScripDataResolver resolver)
    {
        Theme.BeginCardAuto("ScripConfig");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Configuration", ScripAccent);
            ImGui.Spacing();
            ImGui.Spacing();

            var disabled = session.IsActive;
            if (disabled) ImGui.BeginDisabled();

            // Scrip type selection
            ImGui.TextColored(Theme.TextSecondary, "Scrip Type:");
            ImGui.SameLine();

            var typeNames = new[] { "Purple Crafter", "Purple Gatherer", "Orange Crafter", "Orange Gatherer" };
            Theme.PushFrameStyle();
            ImGui.SetNextItemWidth(200);
            if (ImGui.Combo("##ScripType", ref scripTypeIndex, typeNames, typeNames.Length))
            {
                SaveConfig();
                RefreshCollectableList(resolver);
            }
            Theme.PopFrameStyle();

            ImGui.Spacing();

            // Collectable dropdown
            var currentScripType = (ScripType)scripTypeIndex;
            if (cachedScripType != currentScripType || cachedCollectables.Count == 0)
                RefreshCollectableList(resolver);

            ImGui.TextColored(Theme.TextSecondary, "Collectable:");
            ImGui.SameLine();

            Theme.PushFrameStyle();
            ImGui.SetNextItemWidth(300);
            if (cachedNames.Length > 0)
            {
                if (ImGui.Combo("##Collectable", ref selectedCollectableIndex, cachedNames, cachedNames.Length))
                {
                    if (selectedCollectableIndex == 0)
                        selectedCollectableId = 0; // Auto
                    else if (selectedCollectableIndex - 1 < cachedCollectables.Count)
                        selectedCollectableId = cachedCollectables[selectedCollectableIndex - 1].ItemId;
                    SaveConfig();
                }
            }
            else
            {
                var empty = "No collectables found";
                ImGui.InputText("##CollectableEmpty", ref empty, 64, ImGuiInputTextFlags.ReadOnly);
            }
            Theme.PopFrameStyle();

            // Show selected collectable info
            if (selectedCollectableIndex > 0 && selectedCollectableIndex - 1 < cachedCollectables.Count)
            {
                var info = cachedCollectables[selectedCollectableIndex - 1];
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextMuted, $"({info.ScripReward} scrip/item)");
            }

            ImGui.Spacing();

            // Goal and batch size
            ImGui.TextColored(Theme.TextSecondary, "Goal:");
            ImGui.SameLine();
            Theme.PushFrameStyle();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("##Goal", ref goalAmount, 100, 1000))
            {
                goalAmount = Math.Max(1, goalAmount);
                SaveConfig();
            }
            Theme.PopFrameStyle();

            ImGui.SameLine(0, 20);
            ImGui.TextColored(Theme.TextSecondary, "Batch Size:");
            ImGui.SameLine();
            Theme.PushFrameStyle();
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("##Batch", ref batchSize, 1, 5))
            {
                batchSize = Math.Clamp(batchSize, 1, 99);
                SaveConfig();
            }
            Theme.PopFrameStyle();

            // Teleport before crafting (crafter scrip types only)
            if (((ScripType)scripTypeIndex).IsCrafter())
            {
                ImGui.Spacing();
                var teleportEnabled = Expedition.Config.ScripTeleportBeforeCrafting;
                if (ImGui.Checkbox("Teleport before crafting##ScripTeleport", ref teleportEnabled))
                {
                    Expedition.Config.ScripTeleportBeforeCrafting = teleportEnabled;
                    Expedition.Config.Save();
                }

                if (teleportEnabled)
                {
                    ImGui.SameLine(0, 20);
                    ImGui.TextColored(Theme.TextSecondary, "Destination:");
                    ImGui.SameLine();

                    var destNames = new[] { "Eulmore", "FC Estate", "Private Estate", "Apartment" };
                    var destIndex = Expedition.Config.ScripTeleportDestination;
                    Theme.PushFrameStyle();
                    ImGui.SetNextItemWidth(160);
                    if (ImGui.Combo("##ScripTeleportDest", ref destIndex, destNames, destNames.Length))
                    {
                        Expedition.Config.ScripTeleportDestination = destIndex;
                        Expedition.Config.Save();
                    }
                    Theme.PopFrameStyle();
                }
            }

            if (disabled) ImGui.EndDisabled();

            // Progress bar
            ImGui.Spacing();
            var balance = currentScripType.GetBalance();
            var fraction = goalAmount > 0 ? Math.Clamp((float)balance / goalAmount, 0f, 1f) : 0f;
            Theme.ProgressBar(fraction, ScripAccent,
                $"{balance} / {goalAmount} scrips ({fraction * 100:F0}%)", 20);

            ImGui.Spacing();
        }
        Theme.EndCardAuto();
    }

    // ──────────────────────────────────────────────
    // Session Card
    // ──────────────────────────────────────────────

    private static void DrawSessionCard(ScripFarmingSession session)
    {
        Theme.BeginCardAuto("ScripSession");
        {
            ImGui.Spacing();
            Theme.SectionHeader("Session", ScripAccent);
            ImGui.Spacing();
            ImGui.Spacing();

            // State indicator
            var stateColor = session.State switch
            {
                ScripFarmingSession.SessionState.GatheringMaterials or ScripFarmingSession.SessionState.Crafting => Theme.Success,
                ScripFarmingSession.SessionState.WaitingForTurnIn => Theme.Warning,
                ScripFarmingSession.SessionState.Initializing or ScripFarmingSession.SessionState.CheckingGoal => Theme.Accent,
                ScripFarmingSession.SessionState.Paused => Theme.PhasePaused,
                ScripFarmingSession.SessionState.Error => Theme.Error,
                ScripFarmingSession.SessionState.Completed => Theme.PhaseComplete,
                _ => Theme.TextMuted,
            };
            Theme.StatusDot(stateColor, session.StatusMessage.Length > 0 ? session.StatusMessage : "Idle");
            ImGui.Spacing();

            // Stats
            Theme.KeyValue("Duration:", $"  {session.GetDurationString()}");
            Theme.KeyValue("Loops:", $"  {session.LoopCount}");
            Theme.KeyValue("Earned:", $"  +{session.ScripsEarned} scrips");
            Theme.KeyValue("Rate:", $"  {session.ScripsPerHour:F0}/hr");
            Theme.KeyValue("ETA:", $"  {session.GetEtaString()}");

            // Auto-rotation status
            if (session.IsAutoRotation)
            {
                ImGui.Spacing();
                var target = session.CurrentGatherTarget;
                if (target != null)
                {
                    var classTag = target.GatherType == RecipeResolver.GatherType.Miner ? "MIN" : "BTN";
                    ImGui.TextColored(Theme.Success, $"Auto-Rotation: Active [{classTag}]");
                    Theme.KeyValue("  Target:", $"  {target.ItemName} ({target.ScripReward} scrip)");
                }

                // Rotation pool table
                ImGui.Spacing();
                ImGui.TextColored(ScripAccent, "Timed Node Rotation:");
                ImGui.Spacing();

                var poolStatus = session.GetRotationPoolStatus();
                if (poolStatus.Count > 0 && ImGui.BeginTable("##RotationPool", 5,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 18f);
                    ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 32f);
                    ImGui.TableSetupColumn("ET", ImGuiTableColumnFlags.WidthFixed, 50f);
                    ImGui.TableSetupColumn("Window", ImGuiTableColumnFlags.WidthFixed, 90f);
                    ImGui.TableHeadersRow();

                    foreach (var node in poolStatus)
                    {
                        ImGui.TableNextRow();

                        // Status dot
                        ImGui.TableNextColumn();
                        var dotColor = node.IsCurrent ? Theme.Success
                            : node.IsActive ? Theme.Warning
                            : Theme.TextMuted;
                        Theme.StatusDot(dotColor, "");

                        // Node name
                        ImGui.TableNextColumn();
                        var nameColor = node.IsCurrent ? Theme.Success : Theme.TextSecondary;
                        ImGui.TextColored(nameColor, node.ItemName);

                        // Class
                        ImGui.TableNextColumn();
                        var classStr = node.GatherType == RecipeResolver.GatherType.Miner ? "MIN" : "BTN";
                        ImGui.TextColored(Theme.TextMuted, classStr);

                        // ET spawn hours
                        ImGui.TableNextColumn();
                        ImGui.TextColored(Theme.TextMuted, node.SpawnHours);

                        // Window status
                        ImGui.TableNextColumn();
                        if (node.IsCurrent)
                        {
                            ImGui.TextColored(Theme.Success, "Gathering");
                        }
                        else if (node.IsActive)
                        {
                            ImGui.TextColored(Theme.Warning,
                                $"{Scheduling.EorzeanTime.FormatRealDuration(node.RemainingSeconds)} left");
                        }
                        else
                        {
                            ImGui.TextColored(Theme.TextMuted,
                                $"in {Scheduling.EorzeanTime.FormatRealDuration(node.SecondsUntilNext)}");
                        }
                    }

                    ImGui.EndTable();
                }
                else if (poolStatus.Count == 0)
                {
                    ImGui.TextColored(Theme.TextMuted, "  No timed nodes in pool.");
                }
            }
            // Single-target timed node status
            else if (session.SelectedCollectable is { IsTimedNode: true } && session.FallbackCollectable != null)
            {
                ImGui.Spacing();
                if (session.IsGatheringFallback)
                {
                    ImGui.TextColored(Theme.Warning, $"Timed node waiting — gathering {session.FallbackCollectable.ItemName} meanwhile");
                }
                else
                {
                    ImGui.TextColored(Theme.Success, $"Timed window active — gathering {session.SelectedCollectable.ItemName}");
                }
            }

            // Recipe & Ingredients (crafter only)
            if (((ScripType)scripTypeIndex).IsCrafter() && session.SelectedCollectable is { IsCraftable: true })
            {
                ImGui.Spacing();
                Theme.SectionHeader("Recipe", ScripAccent);
                ImGui.Spacing();

                var ingredients = session.GetRecipeIngredients();
                if (ingredients != null && ingredients.Count > 0)
                {
                    // Show how many will actually be crafted based on remaining scrips needed
                    var remaining = session.Goal - session.CurrentBalance;
                    var reward = session.SelectedCollectable.ScripReward;
                    var itemsToGoal = reward > 0 ? (int)Math.Ceiling((double)remaining / reward) : session.BatchSize;
                    var effectiveBatch = Math.Clamp(itemsToGoal, 1, session.BatchSize);
                    ImGui.TextColored(Theme.TextSecondary,
                        $"Crafting {effectiveBatch}x {session.SelectedCollectable.ItemName}" +
                        (effectiveBatch < session.BatchSize ? $"  (need {itemsToGoal} to reach goal)" : ""));
                    ImGui.Spacing();

                    if (ImGui.BeginTable("##RecipeIngredients", 3,
                        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
                    {
                        ImGui.TableSetupColumn("Ingredient", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 50f);
                        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 50f);
                        ImGui.TableHeadersRow();

                        foreach (var (name, needed, owned) in ingredients)
                        {
                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();
                            ImGui.TextColored(Theme.TextSecondary, name);

                            ImGui.TableNextColumn();
                            ImGui.TextColored(Theme.TextMuted, $"{needed}");

                            ImGui.TableNextColumn();
                            var haveColor = owned >= needed ? Theme.Success : Theme.Error;
                            ImGui.TextColored(haveColor, $"{owned}");
                        }

                        ImGui.EndTable();
                    }
                }
            }

            // Requirements
            ImGui.Spacing();
            var gbr = Expedition.Instance?.Ipc.GatherBuddy;
            var artisan = Expedition.Instance?.Ipc.Artisan;

            Theme.StatusDot(
                gbr?.IsAvailable == true ? Theme.Success : Theme.Error,
                gbr?.IsAvailable == true ? "GatherBuddy Reborn: Ready" : "GatherBuddy Reborn: Not Found");

            if (((ScripType)scripTypeIndex).IsCrafter())
            {
                Theme.StatusDot(
                    artisan?.IsAvailable == true ? Theme.Success : Theme.Error,
                    artisan?.IsAvailable == true ? "Artisan: Ready" : "Artisan: Not Found (required for crafter scrips)");
            }

            ImGui.Spacing();
        }
        Theme.EndCardAuto();
    }

    // ──────────────────────────────────────────────
    // Activity Log Card
    // ──────────────────────────────────────────────

    private static void DrawLogCard(ScripFarmingSession session)
    {
        if (Theme.BeginCard("ScripLog", 0))
        {
            ImGui.Spacing();
            Theme.SectionHeader("Activity Log", ScripAccent);
            ImGui.Spacing();

            var log = session.ActivityLog;
            if (log.Count == 0)
            {
                ImGui.TextColored(Theme.TextMuted, "No activity yet. Start a session to see logs.");
            }
            else
            {
                // Show most recent entries (scrolled to bottom)
                for (var i = Math.Max(0, log.Count - 50); i < log.Count; i++)
                    ImGui.TextColored(Theme.TextSecondary, log[i]);

                // Auto-scroll to bottom
                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                    ImGui.SetScrollHereY(1.0f);
            }
        }
        Theme.EndCard();
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static void EnsureConfigLoaded()
    {
        if (configLoaded) return;
        configLoaded = true;

        var config = Expedition.Config;
        scripTypeIndex = config.ScripFarmingType;
        selectedCollectableId = config.ScripFarmingCollectableId;
        goalAmount = config.ScripFarmingGoal;
        batchSize = config.ScripFarmingBatchSize;
    }

    private static void SaveConfig()
    {
        var config = Expedition.Config;
        config.ScripFarmingType = scripTypeIndex;
        config.ScripFarmingCollectableId = selectedCollectableId;
        config.ScripFarmingGoal = goalAmount;
        config.ScripFarmingBatchSize = batchSize;
        config.Save();
    }

    private static void RefreshCollectableList(ScripDataResolver resolver)
    {
        var scripType = (ScripType)scripTypeIndex;
        cachedScripType = scripType;
        cachedCollectables = resolver.GetCollectables(scripType);

        // Build name array with "Auto (Best)" as first option
        var names = new string[cachedCollectables.Count + 1];
        names[0] = "Auto (Best for level)";
        for (var i = 0; i < cachedCollectables.Count; i++)
        {
            var c = cachedCollectables[i];
            var levelInfo = c.IsCraftable ? $"Lv{c.RequiredLevel}" : $"Lv{c.GatherLevel}";
            var timedTag = c.IsTimedNode ? " [Timed]" : "";
            var classTag = c.IsGatherable ? $" [{(c.GatherType == RecipeResolver.GatherType.Miner ? "MIN" : "BTN")}]" : "";
            names[i + 1] = $"{c.ItemName} — {c.ScripReward} scrip ({levelInfo}){classTag}{timedTag}";
        }
        cachedNames = names;

        // Restore selection
        selectedCollectableIndex = 0;
        if (selectedCollectableId != 0)
        {
            for (var i = 0; i < cachedCollectables.Count; i++)
            {
                if (cachedCollectables[i].ItemId == selectedCollectableId)
                {
                    selectedCollectableIndex = i + 1;
                    break;
                }
            }
        }
    }

    private static void TryStartSession(Expedition plugin)
    {
        var scripType = (ScripType)scripTypeIndex;
        var resolver = plugin.ScripDataResolver;
        var session = plugin.ScripFarmingSession;

        var isAutoMode = selectedCollectableIndex == 0 || selectedCollectableId == 0;
        ScripCollectableInfo? collectable;

        if (isAutoMode)
        {
            // Auto-pick best
            var player = DalamudApi.ObjectTable.LocalPlayer;
            var level = player?.Level ?? 100;
            collectable = resolver.RecommendBest(scripType, (int)level);
        }
        else
        {
            collectable = cachedCollectables.FirstOrDefault(c => c.ItemId == selectedCollectableId);
        }

        if (collectable == null)
        {
            DalamudApi.ChatGui.PrintError("[Expedition] No suitable collectable found for the selected scrip type and level.");
            return;
        }

        session.ScripType = scripType;
        session.Goal = goalAmount;
        session.BatchSize = batchSize;
        session.Start(collectable, isAutoMode);
    }
}
