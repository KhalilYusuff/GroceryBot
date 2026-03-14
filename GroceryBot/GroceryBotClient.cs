using GroceryBot.Model;
using System.Diagnostics;
using System.Text.Json;

namespace GroceryBot;

public partial class GroceryBotClient
{
    private static Actions[] BuildActions(GameState state)
    {
        if (state.Round == 0)
        {
            ResetGameTracking();
        }

        var planningStopwatch = Stopwatch.StartNew();
        CurrentRoundCache = CreateRoundCache(state);
        UpdateProgressTracking(state);
        LogMoveOutcomes(state);

        var deliveryTargets = GetDeliveryTargets(state);
        var occupiedPositions = state.Bots
            .Where(bot => bot.Position.Length >= 2)
            .Select(bot => (bot.Position[0], bot.Position[1]))
            .ToHashSet();

        var sortedBots = state.Bots
            .Where(bot => bot.Position.Length >= 2)
            .OrderBy(bot => bot.Id)
            .ToList();
        var offset = sortedBots.Count == 0 ? 0 : state.Round % sortedBots.Count;
        var planningOrder = sortedBots
            .Skip(offset)
            .Concat(sortedBots.Take(offset))
            .ToList();

        var planningContext = new PlanningContext
        {
            OccupiedPositions = occupiedPositions,
            ReservedTargets = [],
            ReservedCorridorCells = [],
            CommittedTypesByOrderId = [],
            ReservedItemIds = [],
            DeliveryReservations = []
        };

        var actions = new List<Actions>(state.Bots.Count);
        var allocationStopwatch = Stopwatch.StartNew();
        var tasksByBotId = AllocateTasks(state, planningOrder, deliveryTargets);
        allocationStopwatch.Stop();
        LastTaskSummary = string.Join(
            " ",
            tasksByBotId.Values
                .GroupBy(task => task.Kind)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}"));
        var executeElapsedMs = 0L;

        foreach (var bot in planningOrder)
        {
            planningContext.OccupiedPositions.Remove((bot.Position[0], bot.Position[1]));

            var task = tasksByBotId.GetValueOrDefault(bot.Id) ?? new BotTask { Kind = "wait" };
            BotResponse response;
            var executeStopwatch = Stopwatch.StartNew();
            if (planningStopwatch.ElapsedMilliseconds >= PlanningSoftLimitMs)
            {
                response = BuildEmergencyResponse(bot, state, deliveryTargets, planningContext);
                LogDebug($"task: bot={bot.Id} role=emergency_fallback");
            }
            else
            {
                LogDebug(
                    $"task: bot={bot.Id} role={task.Kind} target={FormatPoint(task.Target ?? [])} item={task.ItemId ?? "-"}");
                response = ExecuteTask(bot, task, state, planningContext, deliveryTargets);
            }
            executeStopwatch.Stop();
            executeElapsedMs += executeStopwatch.ElapsedMilliseconds;

            var action = response.Actions[0];
            var nextPosition = GetNextPosition(bot.Position, action.Action);
            LastActionAttempts[bot.Id] = (action.Action, action.ItemId);

            if (action.Action != "wait" && nextPosition.Length >= 2)
            {
                planningContext.OccupiedPositions.Add((nextPosition[0], nextPosition[1]));
                LastPlannedMoves[bot.Id] = (action.Action, nextPosition[0], nextPosition[1]);
            }
            else
            {
                LastPlannedMoves.Remove(bot.Id);
            }

            CommitBotPlan(bot, state, action, planningContext);
            actions.Add(action);
        }

        LastPlannerTiming =
            $"timing: round={state.Round} allocate_ms={allocationStopwatch.ElapsedMilliseconds} corridor_ms=0 execute_ms={executeElapsedMs} total_ms={planningStopwatch.ElapsedMilliseconds}";
        MaybeLogRoundSummary(state);

        return actions
            .OrderBy(action => action.BotId)
            .ToArray();
    }

    private static Dictionary<int, BotTask> AllocateTasks(
        GameState state,
        List<Bot> planningOrder,
        List<int[]> deliveryTargets)
    {
        var teamSize = planningOrder.Count;
        var active = state.Orders.FirstOrDefault(o => o.Status == "active");
        if (teamSize >= 10 && active != null)
        {
            return AllocateRoleBasedTasks(state, planningOrder, deliveryTargets, active);
        }

        LastRoleSummary = "legacy";
        return AllocateLegacyTasks(state, planningOrder, deliveryTargets, active);
    }

    private static Dictionary<int, BotTask> AllocateLegacyTasks(
        GameState state,
        List<Bot> planningOrder,
        List<int[]> deliveryTargets,
        Order? active)
    {
        var tasksByBotId = new Dictionary<int, BotTask>();
        var preview = state.Orders.FirstOrDefault(o => o.Status == "preview");
        var endgame = state.Max_rounds > 0 && state.Round >= state.Max_rounds - EndgameRounds;
        var teamSize = planningOrder.Count;
        var smallTeam = teamSize <= 5;
        var largeTeam = teamSize >= 10;
        var recoveryMode = RecoveryRoundsRemaining > 0;

        if (active == null)
        {
            foreach (var bot in planningOrder)
            {
                tasksByBotId[bot.Id] = bot.Inventory.Count > 0
                    ? new BotTask { Kind = "deliver", Target = SelectDeliveryTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList()) }
                    : new BotTask { Kind = "stage", Target = GetBestStageTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList(), endgame) };
            }

            return tasksByBotId;
        }

        var activeDemand = GetOutstandingCounts(active);
        var previewDemand = preview == null ? new Dictionary<string, int>() : GetOutstandingCounts(preview);
        var initialActiveOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        var previewPickupCap = teamSize >= 8 && preview != null
            ? previewDemand.Values.Where(count => count > 0).Sum()
            : 0;
        var availableBots = new List<Bot>(planningOrder);

        foreach (var bot in planningOrder)
        {
            if (bot.Inventory.Count == 0)
            {
                continue;
            }

            var activeMatches = CountMatchingInventory(bot.Inventory, activeDemand);
            if (activeMatches > 0)
            {
                var continuePickupTask = TryAssignContinuedPickupTask(
                    bot,
                    state,
                    activeDemand,
                    active.Id,
                    deliveryTargets);
                ConsumeMatchingInventory(bot.Inventory, activeDemand);

                if (continuePickupTask != null)
                {
                    tasksByBotId[bot.Id] = continuePickupTask;
                    activeDemand[continuePickupTask.ItemType!] = activeDemand.GetValueOrDefault(continuePickupTask.ItemType!) - 1;
                    availableBots.Remove(bot);
                    continue;
                }

                tasksByBotId[bot.Id] = new BotTask
                {
                    Kind = "deliver",
                    Target = SelectDeliveryTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList())
                };
                availableBots.Remove(bot);
                continue;
            }

            if (bot.Inventory.Count >= 3)
            {
                tasksByBotId[bot.Id] = new BotTask
                {
                    Kind = "stage",
                    Target = GetBestStageTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList(), endgame)
                };
                availableBots.Remove(bot);
            }
        }

        foreach (var bot in availableBots.ToList())
        {
            if (bot.Inventory.Count == 0)
            {
                continue;
            }

            if (!smallTeam && !recoveryMode && preview != null && CountMatchingInventory(bot.Inventory, previewDemand) > 0)
            {
                tasksByBotId[bot.Id] = new BotTask
                {
                    Kind = "hold_preview",
                    OrderId = preview.Id,
                    Target = GetBestPreviewHoldTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList())
                };
                ConsumeMatchingInventory(bot.Inventory, previewDemand);
                availableBots.Remove(bot);
                continue;
            }

            // Small teams cannot afford to park partially loaded bots while active demand remains.
            // Keep them available for active pickups until they are full or the active order is covered.
            if (smallTeam &&
                bot.Inventory.Count < 3 &&
                activeDemand.Values.Any(count => count > 0))
            {
                continue;
            }

            tasksByBotId[bot.Id] = new BotTask
            {
                Kind = largeTeam ? "park" : "stage",
                Target = largeTeam
                    ? GetBestParkingTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList())
                    : GetBestStageTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList(), endgame)
            };
            availableBots.Remove(bot);
        }

        var activeAssignments = AssignPickupTasks(availableBots, state, active, activeDemand, deliveryTargets, "pickup_active");
        foreach (var pair in activeAssignments)
        {
            tasksByBotId[pair.Key] = pair.Value;
            availableBots.RemoveAll(bot => bot.Id == pair.Key);
        }

        if (availableBots.Count > 0 && activeDemand.Values.Any(count => count > 0))
        {
            var supportAssignments = AssignActiveSupportTasks(
                availableBots,
                state,
                activeDemand,
                deliveryTargets,
                tasksByBotId.Values.ToList(),
                smallTeam ? availableBots.Count : Math.Min(3, availableBots.Count));
            foreach (var pair in supportAssignments)
            {
                tasksByBotId[pair.Key] = pair.Value;
                availableBots.RemoveAll(bot => bot.Id == pair.Key);
            }
        }

        var activeCovered = activeDemand.Values.All(count => count <= 0);
        var currentActiveOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        var allowPreviewWork = !recoveryMode &&
                               preview != null &&
                               previewPickupCap > 0 &&
                               (!smallTeam
                                   ? activeCovered
                                   : activeCovered &&
                                     initialActiveOutstanding <= 2 &&
                                     currentActiveOutstanding == 0);
        if (allowPreviewWork && preview != null)
        {
            var parallelPreviewCap = activeCovered
                ? previewPickupCap
                : Math.Min(previewPickupCap, Math.Max(1, teamSize / 6));
            var previewAssignments = AssignPickupTasks(availableBots, state, preview, previewDemand, deliveryTargets, "pickup_preview", parallelPreviewCap);
            foreach (var pair in previewAssignments)
            {
                tasksByBotId[pair.Key] = pair.Value;
                availableBots.RemoveAll(bot => bot.Id == pair.Key);
            }
        }

        var assignedTasks = tasksByBotId.Values.ToList();
        foreach (var bot in availableBots)
        {
            var dynamicActiveOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
            var supportTarget = dynamicActiveOutstanding > 0
                ? GetBestActiveSupportTarget(bot, state, activeDemand, deliveryTargets, assignedTasks
                    .Where(task => task.Target != null && task.Target.Length >= 2)
                    .Select(task => (task.Target![0], task.Target[1]))
                    .ToHashSet())
                : null;
            tasksByBotId[bot.Id] = new BotTask
            {
                Kind = dynamicActiveOutstanding > 0 && supportTarget != null
                    ? "support_active"
                    : largeTeam
                        ? "park"
                        : "stage",
                Target = dynamicActiveOutstanding > 0 && supportTarget != null
                    ? supportTarget
                    : largeTeam
                        ? GetBestParkingTarget(bot, state, deliveryTargets, assignedTasks)
                        : GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, endgame)
            };
            assignedTasks.Add(tasksByBotId[bot.Id]);
        }

        return tasksByBotId;
    }

    private static Dictionary<int, BotTask> AllocateRoleBasedTasks(
        GameState state,
        List<Bot> planningOrder,
        List<int[]> deliveryTargets,
        Order active)
    {
        var tasksByBotId = new Dictionary<int, BotTask>();
        var preview = state.Orders.FirstOrDefault(order => order.Status == "preview");
        var endgame = state.Max_rounds > 0 && state.Round >= state.Max_rounds - EndgameRounds;
        var recoveryMode = RecoveryRoundsRemaining > 0;
        var activeDemand = GetOutstandingCounts(active);
        var previewDemand = preview == null ? new Dictionary<string, int>() : GetOutstandingCounts(preview);
        var initialActiveOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        var finishMode = initialActiveOutstanding <= 2;
        var rolesByBotId = BuildRoleAssignments(state, planningOrder, active, preview, activeDemand, previewDemand, deliveryTargets, endgame, recoveryMode);
        var focusTypesByBotId = BuildActiveFocusAssignments(state, planningOrder, rolesByBotId, activeDemand);
        var availableBots = new List<Bot>(planningOrder);
        var activeInventoryFlushesAssigned = 0;

        foreach (var bot in planningOrder)
        {
            if (bot.Inventory.Count == 0)
            {
                continue;
            }

            var role = rolesByBotId.GetValueOrDefault(bot.Id, "reserve");
            var currentActiveOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
            var activeMatches = CountMatchingInventory(bot.Inventory, activeDemand);
            var previewMatches = CountMatchingInventory(bot.Inventory, previewDemand);
            var hasCapacityForActivePickup = bot.Inventory.Count < 3;
            var shouldFlushInventory = bot.Inventory.Count >= 3 ||
                                       StuckRounds.GetValueOrDefault(bot.Id) >= 3 ||
                                       (previewMatches > 0 && !hasCapacityForActivePickup);
            if (activeMatches > 0)
            {
                if (role == "active_picker")
                {
                    var continuePickupTask = TryAssignContinuedPickupTask(
                        bot,
                        state,
                        activeDemand,
                        active.Id,
                        deliveryTargets,
                        focusTypesByBotId.GetValueOrDefault(bot.Id));
                    if (continuePickupTask != null)
                    {
                        tasksByBotId[bot.Id] = continuePickupTask;
                        activeDemand[continuePickupTask.ItemType!] = activeDemand.GetValueOrDefault(continuePickupTask.ItemType!) - 1;
                        ConsumeMatchingInventory(bot.Inventory, activeDemand);
                        availableBots.Remove(bot);
                        continue;
                    }
                }

                tasksByBotId[bot.Id] = new BotTask
                {
                    Kind = "deliver",
                    Target = SelectDeliveryTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList())
                };
                ConsumeMatchingInventory(bot.Inventory, activeDemand);
                availableBots.Remove(bot);
                continue;
            }

            if (role == "courier")
            {
                if (finishMode && currentActiveOutstanding > 0 && activeMatches <= 0)
                {
                    if (hasCapacityForActivePickup)
                    {
                        continue;
                    }

                    tasksByBotId[bot.Id] = new BotTask
                    {
                        Kind = "park",
                        Target = GetBestParkingTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList())
                    };
                    availableBots.Remove(bot);
                    continue;
                }

                var allowCourierFlush = currentActiveOutstanding <= 0 ||
                                        bot.Inventory.Count >= 3 ||
                                        StuckRounds.GetValueOrDefault(bot.Id) >= 3 ||
                                        (!finishMode && activeInventoryFlushesAssigned < 2);
                if (currentActiveOutstanding > 0 && !allowCourierFlush)
                {
                    continue;
                }

                tasksByBotId[bot.Id] = new BotTask
                {
                    Kind = "deliver",
                    Target = SelectDeliveryTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList())
                };
                if (currentActiveOutstanding > 0)
                {
                    activeInventoryFlushesAssigned++;
                }
                availableBots.Remove(bot);
                continue;
            }

            if (currentActiveOutstanding > 0 && hasCapacityForActivePickup)
            {
                continue;
            }

            if (finishMode && currentActiveOutstanding > 0 && activeMatches <= 0)
            {
                tasksByBotId[bot.Id] = new BotTask
                {
                    Kind = role == "reserve" ? "park" : "stage",
                    Target = role == "reserve"
                        ? GetBestParkingTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList())
                        : GetBestStageTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList(), endgame)
                };
                availableBots.Remove(bot);
                continue;
            }

            if (shouldFlushInventory)
            {
                tasksByBotId[bot.Id] = new BotTask
                {
                    Kind = "deliver",
                    Target = SelectDeliveryTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList())
                };
                if (currentActiveOutstanding > 0)
                {
                    activeInventoryFlushesAssigned++;
                }
                availableBots.Remove(bot);
                continue;
            }

            if (bot.Inventory.Count >= 3)
            {
                tasksByBotId[bot.Id] = new BotTask
                {
                    Kind = role == "reserve" ? "park" : "stage",
                    Target = role == "reserve"
                        ? GetBestParkingTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList())
                        : GetBestStageTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList(), endgame)
                };
                availableBots.Remove(bot);
            }
        }

        var activePickers = availableBots
            .Where(bot => rolesByBotId.GetValueOrDefault(bot.Id, "reserve") == "active_picker")
            .ToList();
        var focusedActiveAssignments = AssignFocusedPickupTasks(activePickers, state, active, activeDemand, deliveryTargets, focusTypesByBotId);
        foreach (var pair in focusedActiveAssignments)
        {
            tasksByBotId[pair.Key] = pair.Value;
            availableBots.RemoveAll(bot => bot.Id == pair.Key);
        }

        activePickers = availableBots
            .Where(bot => rolesByBotId.GetValueOrDefault(bot.Id, "reserve") == "active_picker")
            .ToList();
        var activeAssignments = AssignPickupTasks(activePickers, state, active, activeDemand, deliveryTargets, "pickup_active", int.MaxValue, focusTypesByBotId);
        foreach (var pair in activeAssignments)
        {
            tasksByBotId[pair.Key] = pair.Value;
            availableBots.RemoveAll(bot => bot.Id == pair.Key);
        }

        var activeOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        if (finishMode && activeOutstanding > 0)
        {
            var finishBots = availableBots
                .Where(bot => bot.Inventory.Count < 3)
                .OrderBy(bot => rolesByBotId.GetValueOrDefault(bot.Id, "reserve") == "active_picker" ? 0 : 1)
                .ThenBy(bot => rolesByBotId.GetValueOrDefault(bot.Id, "reserve") == "buffer" ? 0 : 1)
                .ThenBy(bot => bot.Id)
                .ToList();
            var finishAssignments = AssignPickupTasks(
                finishBots,
                state,
                active,
                activeDemand,
                deliveryTargets,
                "pickup_active",
                finishBots.Count,
                focusTypesByBotId);
            foreach (var pair in finishAssignments)
            {
                tasksByBotId[pair.Key] = pair.Value;
                availableBots.RemoveAll(bot => bot.Id == pair.Key);
            }

            // In finish mode, one assigned picker can get trapped behind congestion.
            // Add redundant same-type claims for the final 1-2 active items.
            var remainingFinishDemand = GetOutstandingCounts(active);
            foreach (var task in tasksByBotId.Values.Where(task => task.Kind == "pickup_active" && !string.IsNullOrEmpty(task.ItemType)))
            {
                remainingFinishDemand[task.ItemType!] = Math.Max(0, remainingFinishDemand.GetValueOrDefault(task.ItemType!) - 1);
            }

            var unresolvedFinishTypes = remainingFinishDemand
                .Where(pair => pair.Value > 0)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (unresolvedFinishTypes.Count > 0)
            {
                var redundantBots = availableBots
                    .Where(bot => bot.Inventory.Count < 3)
                    .OrderBy(bot => StuckRounds.GetValueOrDefault(bot.Id))
                    .ThenBy(bot => bot.Id)
                    .ToList();
                var redundantAssignments = AssignRedundantFinishPickupTasks(
                    redundantBots,
                    state,
                    active,
                    deliveryTargets,
                    unresolvedFinishTypes,
                    tasksByBotId.Values.ToList(),
                    focusTypesByBotId);
                foreach (var pair in redundantAssignments)
                {
                    tasksByBotId[pair.Key] = pair.Value;
                    availableBots.RemoveAll(bot => bot.Id == pair.Key);
                }
            }
        }

        activeOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        var bufferPickers = availableBots
            .Where(bot => rolesByBotId.GetValueOrDefault(bot.Id, "reserve") == "buffer")
            .ToList();
        if (!finishMode && bufferPickers.Count > 0 && activeOutstanding > 0)
        {
            var bufferPickupAssignments = AssignFocusedPickupTasks(bufferPickers, state, active, activeDemand, deliveryTargets, focusTypesByBotId);
            foreach (var pair in bufferPickupAssignments)
            {
                tasksByBotId[pair.Key] = pair.Value;
                availableBots.RemoveAll(bot => bot.Id == pair.Key);
            }
        }

        activeOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        var extraPickers = availableBots
            .Where(bot =>
                rolesByBotId.GetValueOrDefault(bot.Id, "reserve") != "reserve" &&
                bot.Inventory.Count < 3)
            .ToList();
        if (!finishMode && extraPickers.Count > 0 && activeOutstanding > 0)
        {
            var extraPickupAssignments = AssignPickupTasks(
                extraPickers,
                state,
                active,
                activeDemand,
                deliveryTargets,
                "pickup_active",
                Math.Min(Math.Max(2, activeOutstanding), extraPickers.Count),
                focusTypesByBotId);
            foreach (var pair in extraPickupAssignments)
            {
                tasksByBotId[pair.Key] = pair.Value;
                availableBots.RemoveAll(bot => bot.Id == pair.Key);
            }
        }

        activeOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        var buffers = availableBots
            .Where(bot => rolesByBotId.GetValueOrDefault(bot.Id, "reserve") == "buffer")
            .ToList();
        if (!finishMode && buffers.Count > 0 && activeOutstanding > 0)
        {
            var bufferAssignments = AssignActiveSupportTasks(
                buffers,
                state,
                activeDemand,
                deliveryTargets,
                tasksByBotId.Values.ToList(),
                buffers.Count,
                focusTypesByBotId);
            foreach (var pair in bufferAssignments)
            {
                tasksByBotId[pair.Key] = pair.Value;
                availableBots.RemoveAll(bot => bot.Id == pair.Key);
            }
        }

        activeOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        if (!finishMode && availableBots.Count > 0 && activeOutstanding > 0)
        {
            var extraSupportBots = availableBots
                .Where(bot => rolesByBotId.GetValueOrDefault(bot.Id, "reserve") != "reserve")
                .ToList();
            var extraSupportAssignments = AssignActiveSupportTasks(
                extraSupportBots,
                state,
                activeDemand,
                deliveryTargets,
                tasksByBotId.Values.ToList(),
                extraSupportBots.Count,
                focusTypesByBotId);
            foreach (var pair in extraSupportAssignments)
            {
                tasksByBotId[pair.Key] = pair.Value;
                availableBots.RemoveAll(bot => bot.Id == pair.Key);
            }
        }

        activeOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        if (!finishMode && availableBots.Count > 0 && activeOutstanding > 0)
        {
            var idleCouriers = availableBots
                .Where(bot => rolesByBotId.GetValueOrDefault(bot.Id, "reserve") == "courier")
                .ToList();
            var courierSupportAssignments = AssignActiveSupportTasks(
                idleCouriers,
                state,
                activeDemand,
                deliveryTargets,
                tasksByBotId.Values.ToList(),
                Math.Min(2, idleCouriers.Count),
                focusTypesByBotId);
            foreach (var pair in courierSupportAssignments)
            {
                tasksByBotId[pair.Key] = pair.Value;
                availableBots.RemoveAll(bot => bot.Id == pair.Key);
            }
        }

        var assignedTasks = tasksByBotId.Values.ToList();
        foreach (var bot in availableBots)
        {
            var role = rolesByBotId.GetValueOrDefault(bot.Id, "reserve");
            var dynamicActiveOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
            var reservedTargets = assignedTasks
                .Where(task => task.Target != null && task.Target.Length >= 2)
                .Select(task => (task.Target![0], task.Target[1]))
                .ToHashSet();
            var fallbackPickupTask = dynamicActiveOutstanding > 0 && bot.Inventory.Count < 3
                ? BuildFallbackActivePickupTask(
                    bot,
                    state,
                    active,
                    activeDemand,
                    deliveryTargets,
                    focusTypesByBotId.GetValueOrDefault(bot.Id),
                    reservedTargets)
                : null;
            tasksByBotId[bot.Id] = role switch
            {
                "courier" => new BotTask
                {
                    Kind = "stage",
                    Target = GetBestCourierQueueTarget(bot, state, deliveryTargets, assignedTasks)
                },
                "buffer" => new BotTask
                {
                    Kind = fallbackPickupTask?.Kind ?? "support_active",
                    OrderId = fallbackPickupTask?.OrderId,
                    ItemId = fallbackPickupTask?.ItemId,
                    ItemType = fallbackPickupTask?.ItemType,
                    Target = fallbackPickupTask?.Target ?? GetBestActiveSupportTarget(
                        bot,
                        state,
                        activeDemand,
                        deliveryTargets,
                        reservedTargets,
                        focusTypesByBotId.GetValueOrDefault(bot.Id)) ?? GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, endgame)
                },
                "preview_picker" when preview != null => new BotTask
                {
                    Kind = fallbackPickupTask?.Kind ?? "support_active",
                    OrderId = fallbackPickupTask?.OrderId,
                    ItemId = fallbackPickupTask?.ItemId,
                    ItemType = fallbackPickupTask?.ItemType,
                    Target = fallbackPickupTask?.Target ?? GetBestActiveSupportTarget(
                        bot,
                        state,
                        activeDemand,
                        deliveryTargets,
                        reservedTargets,
                        focusTypesByBotId.GetValueOrDefault(bot.Id)) ?? GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, endgame)
                },
                "active_picker" => new BotTask
                {
                    Kind = fallbackPickupTask?.Kind ?? (dynamicActiveOutstanding > 0 ? "support_active" : "stage"),
                    OrderId = fallbackPickupTask?.OrderId,
                    ItemId = fallbackPickupTask?.ItemId,
                    ItemType = fallbackPickupTask?.ItemType,
                    Target = fallbackPickupTask?.Target ?? (dynamicActiveOutstanding > 0
                        ? GetBestActiveSupportTarget(bot, state, activeDemand, deliveryTargets, reservedTargets, focusTypesByBotId.GetValueOrDefault(bot.Id)) ?? GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, endgame)
                        : GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, endgame)
                    )
                },
                _ => new BotTask
                {
                    Kind = fallbackPickupTask?.Kind ?? (dynamicActiveOutstanding > 0 ? "support_active" : "park"),
                    OrderId = fallbackPickupTask?.OrderId,
                    ItemId = fallbackPickupTask?.ItemId,
                    ItemType = fallbackPickupTask?.ItemType,
                    Target = fallbackPickupTask?.Target ?? (dynamicActiveOutstanding > 0
                        ? GetBestActiveSupportTarget(bot, state, activeDemand, deliveryTargets, reservedTargets, focusTypesByBotId.GetValueOrDefault(bot.Id)) ?? GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, endgame)
                        : GetBestParkingTarget(bot, state, deliveryTargets, assignedTasks)
                    )
                }
            };
            assignedTasks.Add(tasksByBotId[bot.Id]);
        }

        return tasksByBotId;
    }

    private static Dictionary<int, string> BuildRoleAssignments(
        GameState state,
        List<Bot> planningOrder,
        Order active,
        Order? preview,
        Dictionary<string, int> activeDemand,
        Dictionary<string, int> previewDemand,
        List<int[]> deliveryTargets,
        bool endgame,
        bool recoveryMode)
    {
        var rolesByBotId = new Dictionary<int, string>();
        var activeBotIds = planningOrder.Select(bot => bot.Id).ToHashSet();
        foreach (var botId in RoleAssignments.Keys.Where(botId => !activeBotIds.Contains(botId)).ToList())
        {
            RoleAssignments.Remove(botId);
        }

        var teamSize = planningOrder.Count;
        var activeOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        var carryingActive = planningOrder.Count(bot => CountMatchingInventory(bot.Inventory, activeDemand) > 0);
        var hasPreview = false;
        var baseCourierCap = teamSize >= 18
            ? 3
            : 2;
        var adaptiveCourierCap = carryingActive >= Math.Max(4, teamSize / 4)
            ? baseCourierCap + 1
            : baseCourierCap;
        var caps = new Dictionary<string, int>
        {
            ["courier"] = Math.Clamp(adaptiveCourierCap + (activeOutstanding <= 2 ? 1 : 0), 2, 5),
            ["active_picker"] = Math.Clamp(Math.Max(6, teamSize / 2 - 1 + (activeOutstanding >= 6 ? 1 : 0)), 6, 10),
            ["preview_picker"] = 0,
            ["buffer"] = Math.Clamp(teamSize / 7, 2, 3)
        };
        if (endgame)
        {
            caps["preview_picker"] = 0;
            caps["courier"] = Math.Clamp(caps["courier"] + 1, 3, 6);
        }

        while (caps.Values.Sum() > teamSize)
        {
            if (caps["preview_picker"] > 0)
            {
                caps["preview_picker"]--;
                continue;
            }

            if (caps["buffer"] > 0)
            {
                caps["buffer"]--;
                continue;
            }

            if (caps["active_picker"] > 6)
            {
                caps["active_picker"]--;
                continue;
            }

            break;
        }

        var counts = new Dictionary<string, int>
        {
            ["courier"] = 0,
            ["active_picker"] = 0,
            ["preview_picker"] = 0,
            ["buffer"] = 0,
            ["reserve"] = 0
        };

        bool TryAssignRole(Bot bot, string role, bool force = false)
        {
            if (rolesByBotId.ContainsKey(bot.Id))
            {
                return false;
            }

            if (!force && role != "reserve" && counts.GetValueOrDefault(role) >= caps.GetValueOrDefault(role))
            {
                return false;
            }

            rolesByBotId[bot.Id] = role;
            counts[role] = counts.GetValueOrDefault(role) + 1;
            var existing = RoleAssignments.GetValueOrDefault(bot.Id);
            var lockUntil = existing != null && existing.Role == role
                ? existing.LockedUntilRound
                : state.Round + RoleLockRounds;
            RoleAssignments[bot.Id] = new RoleAssignment
            {
                Role = role,
                LockedUntilRound = lockUntil,
                FocusItemType = existing?.FocusItemType
            };
            return true;
        }

        foreach (var bot in planningOrder)
        {
            if (!RoleAssignments.TryGetValue(bot.Id, out var existing))
            {
                continue;
            }

            if (state.Round >= existing.LockedUntilRound)
            {
                continue;
            }

            if (existing.Role == "preview_picker" && !hasPreview)
            {
                continue;
            }

            TryAssignRole(bot, existing.Role);
        }

        foreach (var bot in planningOrder
                     .Where(bot => !rolesByBotId.ContainsKey(bot.Id))
                     .OrderByDescending(bot => CountMatchingInventory(bot.Inventory, activeDemand))
                     .ThenByDescending(bot => bot.Inventory.Count))
        {
            var activeMatches = CountMatchingInventory(bot.Inventory, activeDemand);
            if (activeMatches <= 0)
            {
                continue;
            }

            if (!TryAssignRole(bot, "courier"))
            {
                TryAssignRole(bot, "active_picker");
            }
        }

        foreach (var bot in planningOrder
                     .Where(bot => !rolesByBotId.ContainsKey(bot.Id))
                     .OrderByDescending(bot => bot.Inventory.Count)
                     .ThenBy(bot => deliveryTargets.Count == 0
                         ? int.MaxValue
                         : deliveryTargets.Min(zone => GetPathDistance(bot.Position, zone, state))))
        {
            if (bot.Inventory.Count == 0)
            {
                continue;
            }

            if (!TryAssignRole(bot, "courier"))
            {
                break;
            }
        }

        foreach (var bot in planningOrder
                     .Where(bot => !rolesByBotId.ContainsKey(bot.Id))
                     .OrderBy(bot => state.Items
                         .Where(item => activeDemand.GetValueOrDefault(item.Type) > 0)
                         .Select(item => GetPathDistance(bot.Position, item.Position, state))
                         .DefaultIfEmpty(int.MaxValue)
                         .Min())
                     .ThenBy(bot => bot.Id))
        {
            if (!TryAssignRole(bot, "active_picker"))
            {
                break;
            }
        }

        if (hasPreview)
        {
            foreach (var bot in planningOrder
                         .Where(bot => !rolesByBotId.ContainsKey(bot.Id))
                         .OrderByDescending(bot => CountMatchingInventory(bot.Inventory, previewDemand))
                         .ThenBy(bot => bot.Id))
            {
                if (!TryAssignRole(bot, "preview_picker"))
                {
                    break;
                }
            }
        }

        foreach (var bot in planningOrder
                     .Where(bot => !rolesByBotId.ContainsKey(bot.Id))
                     .OrderBy(bot => state.Items
                         .Where(item => activeDemand.GetValueOrDefault(item.Type) > 0)
                         .Select(item => GetPathDistance(bot.Position, item.Position, state))
                         .DefaultIfEmpty(int.MaxValue)
                         .Min())
                     .ThenBy(bot => bot.Id))
        {
            if (!TryAssignRole(bot, "buffer"))
            {
                break;
            }
        }

        foreach (var bot in planningOrder.Where(bot => !rolesByBotId.ContainsKey(bot.Id)))
        {
            TryAssignRole(bot, "reserve", true);
        }

        LastRoleSummary = string.Join(
            " ",
            rolesByBotId
                .Values
                .GroupBy(role => role)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}"));
        return rolesByBotId;
    }

    private static BotTask? BuildFallbackActivePickupTask(
        Bot bot,
        GameState state,
        Order active,
        Dictionary<string, int> activeDemand,
        List<int[]> deliveryTargets,
        string? preferredItemType,
        HashSet<(int X, int Y)> reservedTargets)
    {
        RecentFallbackPickupAttempts++;
        var candidate = FindBestPickupCandidate(
            bot,
            state.Items,
            activeDemand,
            state,
            deliveryTargets,
            [],
            true,
            preferredItemType,
            reservedTargets);
        if (candidate == null)
        {
            RecentFallbackPickupNoCandidate++;
            if (RecentFallbackPickupNoCandidate <= 6)
            {
                var outstandingTypes = string.Join(
                    ",",
                    activeDemand.Where(pair => pair.Value > 0)
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}"));
                LogLine(
                    $"fallback_pickup_miss: bot={bot.Id} preferred={preferredItemType ?? "-"} inventory={bot.Inventory.Count} stuck={StuckRounds.GetValueOrDefault(bot.Id)} outstanding=[{outstandingTypes}]");
            }
            return null;
        }

        RecentFallbackPickupSuccesses++;

        return new BotTask
        {
            Kind = "pickup_active",
            OrderId = active.Id,
            ItemId = candidate.Item.Id,
            ItemType = candidate.Item.Type,
            Target = candidate.AccessTarget
        };
    }

    private static Dictionary<int, BotTask> AssignFocusedPickupTasks(
        List<Bot> candidateBots,
        GameState state,
        Order order,
        Dictionary<string, int> demand,
        List<int[]> deliveryTargets,
        Dictionary<int, string> preferredTypesByBotId)
    {
        var tasks = new Dictionary<int, BotTask>();
        if (candidateBots.Count == 0 || preferredTypesByBotId.Count == 0)
        {
            return tasks;
        }

        var usedItemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bot in candidateBots
                     .Where(bot => bot.Inventory.Count < 3)
                     .OrderBy(bot => bot.Id))
        {
            var preferredType = preferredTypesByBotId.GetValueOrDefault(bot.Id);
            if (string.IsNullOrEmpty(preferredType) || demand.GetValueOrDefault(preferredType) <= 0)
            {
                continue;
            }

            var strictDemand = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [preferredType] = demand[preferredType]
            };
            var candidate = FindBestPickupCandidate(
                bot,
                state.Items.Where(item => string.Equals(item.Type, preferredType, StringComparison.Ordinal)),
                strictDemand,
                state,
                deliveryTargets,
                usedItemIds,
                true,
                preferredType);
            if (candidate == null)
            {
                continue;
            }

            tasks[bot.Id] = new BotTask
            {
                Kind = "pickup_active",
                OrderId = order.Id,
                ItemId = candidate.Item.Id,
                ItemType = candidate.Item.Type,
                Target = candidate.AccessTarget
            };
            usedItemIds.Add(candidate.Item.Id);
            demand[preferredType]--;
        }

        return tasks;
    }

    private static Dictionary<int, BotTask> AssignRedundantFinishPickupTasks(
        List<Bot> candidateBots,
        GameState state,
        Order order,
        List<int[]> deliveryTargets,
        HashSet<string> unresolvedTypes,
        List<BotTask> assignedTasks,
        Dictionary<int, string> preferredTypesByBotId)
    {
        var tasks = new Dictionary<int, BotTask>();
        if (candidateBots.Count == 0 || unresolvedTypes.Count == 0)
        {
            return tasks;
        }

        var reservedTargets = assignedTasks
            .Where(task => task.Target != null && task.Target.Length >= 2)
            .Select(task => (task.Target![0], task.Target[1]))
            .ToHashSet();
        var usedItemIds = assignedTasks
            .Where(task => !string.IsNullOrEmpty(task.ItemId))
            .Select(task => task.ItemId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var bot in candidateBots)
        {
            var preferredType = preferredTypesByBotId.GetValueOrDefault(bot.Id);
            var candidate = FindBestPickupCandidate(
                bot,
                state.Items.Where(item => unresolvedTypes.Contains(item.Type)),
                unresolvedTypes.ToDictionary(type => type, _ => 1, StringComparer.Ordinal),
                state,
                deliveryTargets,
                usedItemIds,
                true,
                preferredType,
                reservedTargets);
            if (candidate == null)
            {
                continue;
            }

            tasks[bot.Id] = new BotTask
            {
                Kind = "pickup_active",
                OrderId = order.Id,
                ItemId = candidate.Item.Id,
                ItemType = candidate.Item.Type,
                Target = candidate.AccessTarget
            };
            reservedTargets.Add((candidate.AccessTarget[0], candidate.AccessTarget[1]));
            usedItemIds.Add(candidate.Item.Id);

            if (tasks.Count >= Math.Min(2, unresolvedTypes.Count * 2))
            {
                break;
            }
        }

        return tasks;
    }

    private static Dictionary<int, string> BuildActiveFocusAssignments(
        GameState state,
        List<Bot> planningOrder,
        Dictionary<int, string> rolesByBotId,
        Dictionary<string, int> activeDemand)
    {
        var focusesByBotId = new Dictionary<int, string>();
        var demandTypes = activeDemand
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .ToList();
        if (demandTypes.Count == 0)
        {
            return focusesByBotId;
        }

        foreach (var bot in planningOrder)
        {
            var role = rolesByBotId.GetValueOrDefault(bot.Id, "reserve");
            if (role != "active_picker" && role != "buffer")
            {
                continue;
            }

            var persistedFocus = RoleAssignments.GetValueOrDefault(bot.Id)?.FocusItemType;
            if (!string.IsNullOrEmpty(persistedFocus) && activeDemand.GetValueOrDefault(persistedFocus) > 0)
            {
                focusesByBotId[bot.Id] = persistedFocus;
                continue;
            }

            var selectedFocus = demandTypes
                .Select(type => new
                {
                    Type = type,
                    Distance = state.Items
                        .Where(item => string.Equals(item.Type, type, StringComparison.Ordinal))
                        .Select(item => GetPathDistance(bot.Position, item.Position, state))
                        .DefaultIfEmpty(int.MaxValue)
                        .Min()
                })
                .OrderBy(candidate => candidate.Distance)
                .ThenByDescending(candidate => activeDemand.GetValueOrDefault(candidate.Type))
                .ThenBy(candidate => candidate.Type, StringComparer.Ordinal)
                .Select(candidate => candidate.Type)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(selectedFocus))
            {
                continue;
            }

            focusesByBotId[bot.Id] = selectedFocus;
            if (RoleAssignments.TryGetValue(bot.Id, out var existing))
            {
                RoleAssignments[bot.Id] = new RoleAssignment
                {
                    Role = existing.Role,
                    LockedUntilRound = existing.LockedUntilRound,
                    FocusItemType = selectedFocus
                };
            }
        }

        return focusesByBotId;
    }

    private static Dictionary<int, BotTask> AssignPickupTasks(
        List<Bot> candidateBots,
        GameState state,
        Order order,
        Dictionary<string, int> demand,
        List<int[]> deliveryTargets,
        string taskKind,
        int maxAssignments = int.MaxValue,
        Dictionary<int, string>? preferredTypesByBotId = null)
    {
        var tasks = new Dictionary<int, BotTask>();
        if (candidateBots.Count == 0 || maxAssignments <= 0)
        {
            return tasks;
        }

        var candidateItems = state.Items
            .Where(item => demand.GetValueOrDefault(item.Type) > 0)
            .ToList();

        var usedBotIds = new HashSet<int>();
        var usedItemIds = new HashSet<string>();

        while (tasks.Count < maxAssignments)
        {
            Bot? bestBot = null;
            PickupCandidate? bestCandidate = null;

            foreach (var bot in candidateBots)
            {
                if (usedBotIds.Contains(bot.Id) || bot.Inventory.Count >= 3)
                {
                    continue;
                }

                var candidate = FindBestPickupCandidate(
                    bot,
                    candidateItems,
                    demand,
                    state,
                    deliveryTargets,
                    usedItemIds,
                    taskKind == "pickup_active",
                    preferredTypesByBotId?.GetValueOrDefault(bot.Id));
                if (candidate == null)
                {
                    continue;
                }

                if (bestCandidate != null && candidate.Score >= bestCandidate.Score)
                {
                    continue;
                }

                bestBot = bot;
                bestCandidate = candidate;
            }

            if (bestBot == null || bestCandidate == null)
            {
                break;
            }

            tasks[bestBot.Id] = new BotTask
            {
                Kind = taskKind,
                OrderId = order.Id,
                ItemId = bestCandidate.Item.Id,
                ItemType = bestCandidate.Item.Type,
                Target = bestCandidate.AccessTarget
            };
            usedBotIds.Add(bestBot.Id);
            usedItemIds.Add(bestCandidate.Item.Id);
            demand[bestCandidate.Item.Type] = demand.GetValueOrDefault(bestCandidate.Item.Type) - 1;
        }

        return tasks;
    }

    private static Dictionary<int, BotTask> AssignActiveSupportTasks(
        List<Bot> candidateBots,
        GameState state,
        Dictionary<string, int> activeDemand,
        List<int[]> deliveryTargets,
        List<BotTask> assignedTasks,
        int maxAssignments,
        Dictionary<int, string>? preferredTypesByBotId = null)
    {
        var tasks = new Dictionary<int, BotTask>();
        var reservedTargets = assignedTasks
            .Where(task => task.Target != null && task.Target.Length >= 2)
            .Select(task => (task.Target![0], task.Target[1]))
            .ToHashSet();

        foreach (var bot in candidateBots)
        {
            if (tasks.Count >= maxAssignments || bot.Inventory.Count >= 3)
            {
                continue;
            }

            var target = GetBestActiveSupportTarget(bot, state, activeDemand, deliveryTargets, reservedTargets, preferredTypesByBotId?.GetValueOrDefault(bot.Id));
            if (target == null)
            {
                continue;
            }

            tasks[bot.Id] = new BotTask
            {
                Kind = "support_active",
                Target = target
            };
            reservedTargets.Add((target[0], target[1]));
        }

        return tasks;
    }

    private static BotTask? TryAssignContinuedPickupTask(
        Bot bot,
        GameState state,
        Dictionary<string, int> activeDemand,
        string orderId,
        List<int[]> deliveryTargets,
        string? preferredItemType = null)
    {
        if (bot.Inventory.Count >= 3)
        {
            return null;
        }

        var remainingDemand = new Dictionary<string, int>(activeDemand, StringComparer.Ordinal);
        ConsumeMatchingInventory(bot.Inventory, remainingDemand);
        if (remainingDemand.Values.All(count => count <= 0))
        {
            return null;
        }

        var candidate = FindBestPickupCandidate(
            bot,
            state.Items,
            remainingDemand,
            state,
            deliveryTargets,
            [],
            true,
            preferredItemType);
        if (candidate == null)
        {
            return null;
        }

        var deliveryTarget = SelectDeliveryTarget(bot, state, deliveryTargets, []);
        if (deliveryTarget.Length < 2)
        {
            return null;
        }

        var directDeliveryDistance = GetPathDistance(bot.Position, deliveryTarget, state);
        if (directDeliveryDistance == int.MaxValue)
        {
            return null;
        }

        var pickupThenDeliverDistance = candidate.PickupDistance + candidate.DeliveryDistance;
        var allowedDetour = 4 + ((3 - bot.Inventory.Count) * 2);
        if (pickupThenDeliverDistance > directDeliveryDistance + allowedDetour)
        {
            return null;
        }

        return new BotTask
        {
            Kind = "pickup_active",
            OrderId = orderId,
            ItemId = candidate.Item.Id,
            ItemType = candidate.Item.Type,
            Target = candidate.AccessTarget
        };
    }

    private static BotResponse ExecuteTask(
        Bot bot,
        BotTask task,
        GameState state,
        PlanningContext planningContext,
        List<int[]> deliveryTargets)
    {
        return task.Kind switch
        {
            "deliver" => ExecuteDeliverTask(bot, state, planningContext, task.Target ?? GetBestTarget(bot.Position, deliveryTargets, state), task.Kind),
            "pickup_active" => ExecutePickupTask(bot, state, planningContext, task),
            "pickup_preview" => ExecutePickupTask(bot, state, planningContext, task),
            "support_active" => ExecuteSupportActiveTask(bot, state, planningContext, task),
            "hold_preview" => ExecuteMoveTask(
                bot,
                state,
                planningContext,
                task.Target ?? GetBestPreviewHoldTarget(bot, state, deliveryTargets, []),
                false,
                task.Kind),
            "park" => ExecuteParkTask(bot, state, planningContext, task, deliveryTargets),
            "stage" => ExecuteMoveTask(bot, state, planningContext, task.Target ?? [], false, task.Kind),
            _ => CreateResponse(bot.Id, FindRecoveryAction(bot, state, planningContext.OccupiedPositions, task.Target ?? []))
        };
    }

    private static BotResponse ExecuteDeliverTask(
        Bot bot,
        GameState state,
        PlanningContext planningContext,
        int[] deliveryTarget,
        string role)
    {
        if (IsAtAnyTarget(bot.Position, GetDeliveryTargets(state)))
        {
            CourierRoutes.Remove(bot.Id);
            CourierTargets.Remove(bot.Id);
            return CreateResponse(bot.Id, "drop_off");
        }

        var response = ExecuteDeliverMoveTask(bot, state, planningContext, deliveryTarget, role);
        var action = response.Actions[0].Action;
        var nextPosition = GetNextPosition(bot.Position, action);
        AdvanceCourierRoute(bot.Id, nextPosition);
        return response;
    }

    private static BotResponse BuildEmergencyResponse(
        Bot bot,
        GameState state,
        List<int[]> deliveryTargets,
        PlanningContext planningContext)
    {
        if (bot.Inventory.Count > 0)
        {
            if (IsAtAnyTarget(bot.Position, deliveryTargets))
            {
                return CreateResponse(bot.Id, "drop_off");
            }

            var target = GetBestTarget(bot.Position, deliveryTargets, state);
            return ExecuteMoveTask(bot, state, planningContext, target, true, "emergency_deliver");
        }

        var active = state.Orders.FirstOrDefault(order => order.Status == "active");
        if (active == null)
        {
            return CreateResponse(bot.Id, "wait");
        }

        var needed = GetOutstandingNeeded(active);
        foreach (var item in state.Items)
        {
            if (!needed.Contains(item.Type))
            {
                continue;
            }

            var distance = Math.Abs(bot.Position[0] - item.Position[0]) + Math.Abs(bot.Position[1] - item.Position[1]);
            if (distance == 1)
            {
                return CreateResponse(bot.Id, "pick_up", item.Id);
            }
        }

        var targetItem = state.Items
            .Where(item => needed.Contains(item.Type))
            .Select(item => new { Target = FindAdjacentWalkableTarget(item.Position, state, planningContext.OccupiedPositions, planningContext.ReservedTargets, bot.Position), item.Id })
            .Where(candidate => candidate.Target != null)
            .OrderBy(candidate => GetHeuristicDistance(bot.Position, candidate.Target!))
            .FirstOrDefault();

        if (targetItem?.Target != null)
        {
            return ExecuteMoveTask(bot, state, planningContext, targetItem.Target, false, "emergency_pickup");
        }

        return CreateResponse(bot.Id, "wait");
    }

    private static BotResponse ExecutePickupTask(
        Bot bot,
        GameState state,
        PlanningContext planningContext,
        BotTask task)
    {
        if (string.IsNullOrEmpty(task.ItemId))
        {
            return CreateResponse(bot.Id, "wait");
        }

        var item = state.Items.FirstOrDefault(candidate => candidate.Id == task.ItemId);
        if (item == null)
        {
            return CreateResponse(bot.Id, "wait");
        }

        if (bot.Position.Length >= 2 && item.Position.Length >= 2)
        {
            var distance = Math.Abs(bot.Position[0] - item.Position[0]) + Math.Abs(bot.Position[1] - item.Position[1]);
            if (distance == 1)
            {
                PickupAccessTargets.Remove(bot.Id);
                return CreateResponse(bot.Id, "pick_up", item.Id);
            }
        }

        var target = task.Target != null && task.Target.Length >= 2 && IsAccessiblePickupTarget(bot, task.Target, state, planningContext)
            ? task.Target
            : GetPickupAccessTarget(bot, item, state, planningContext);
        if (target == null)
        {
            PickupAccessTargets.Remove(bot.Id);
            return CreateResponse(bot.Id, FindRecoveryAction(bot, state, planningContext.OccupiedPositions, []));
        }

        planningContext.ReservedTargets.Add((target[0], target[1]));
        return ExecuteMoveTask(bot, state, planningContext, target, false, task.Kind);
    }

    private static BotResponse ExecuteParkTask(
        Bot bot,
        GameState state,
        PlanningContext planningContext,
        BotTask task,
        List<int[]> deliveryTargets)
    {
        var target = task.Target ?? GetBestParkingTarget(bot, state, deliveryTargets, []);
        if (target.Length < 2)
        {
            return CreateResponse(bot.Id, "wait");
        }

        if (bot.Position.Length >= 2 && bot.Position[0] == target[0] && bot.Position[1] == target[1])
        {
            return CreateResponse(bot.Id, "wait");
        }

        return ExecuteMoveTask(bot, state, planningContext, target, false, task.Kind);
    }

    private static BotResponse ExecuteSupportActiveTask(
        Bot bot,
        GameState state,
        PlanningContext planningContext,
        BotTask task)
    {
        var active = state.Orders.FirstOrDefault(order => order.Status == "active");
        if (active != null && bot.Inventory.Count < 3)
        {
            var needed = GetOutstandingNeeded(active);
            var adjacentItem = state.Items
                .Where(item => needed.Contains(item.Type))
                .Where(item => item.Position.Length >= 2)
                .Where(item => Math.Abs(bot.Position[0] - item.Position[0]) + Math.Abs(bot.Position[1] - item.Position[1]) == 1)
                .OrderBy(item => item.Type)
                .ThenBy(item => item.Id)
                .FirstOrDefault();

            if (adjacentItem != null)
            {
                return CreateResponse(bot.Id, "pick_up", adjacentItem.Id);
            }
        }

        return ExecuteMoveTask(bot, state, planningContext, task.Target ?? [], false, task.Kind);
    }

    private static void CommitBotPlan(Bot bot, GameState state, Actions action, PlanningContext planningContext)
    {
        if (action.Action == "pick_up")
        {
            PickupAccessTargets.Remove(bot.Id);
            return;
        }

        if (action.Action.StartsWith("move_", StringComparison.Ordinal))
        {
            var nextPosition = GetNextPosition(bot.Position, action.Action);
            if (nextPosition.Length >= 2)
            {
                planningContext.ReservedTargets.Add((nextPosition[0], nextPosition[1]));
            }
        }

        if (action.Action == "drop_off")
        {
            CourierRoutes.Remove(bot.Id);
            CourierTargets.Remove(bot.Id);
        }
    }

    private static void PopulateFallbackFields(GameState state, string payloadText)
    {
        using var document = JsonDocument.Parse(payloadText);
        var root = document.RootElement;

        if (state.Drop_off_zones.Count == 0 &&
            root.TryGetProperty("drop_off_zones", out var dropOffZonesElement) &&
            dropOffZonesElement.ValueKind == JsonValueKind.Array)
        {
            state.Drop_off_zones = ReadPointList(dropOffZonesElement);
            LogLine($"fallback: mapped drop_off_zones={state.Drop_off_zones.Count}");
        }

        if (state.Drop_off.Length == 0 &&
            root.TryGetProperty("drop_off", out var dropOffElement) &&
            dropOffElement.ValueKind == JsonValueKind.Array)
        {
            state.Drop_off = ReadPoint(dropOffElement);
        }
    }

    private static List<int[]> ReadPointList(JsonElement arrayElement)
    {
        var result = new List<int[]>();
        foreach (var child in arrayElement.EnumerateArray())
        {
            var point = ReadPoint(child);
            if (point.Length >= 2)
            {
                result.Add(point);
            }
        }

        return result;
    }

    private static int[] ReadPoint(JsonElement pointElement)
    {
        if (pointElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<int>();
        foreach (var value in pointElement.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                values.Add(number);
            }
        }

        return values.ToArray();
    }

    private static List<string> GetOutstandingNeeded(Order order)
    {
        var outstandingNeeded = new List<string>(order.Items_required);

        foreach (var delivered in order.Items_delivered)
        {
            outstandingNeeded.Remove(delivered);
        }

        return outstandingNeeded;
    }

    private static Dictionary<string, int> GetOutstandingCounts(Order order)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in order.Items_required)
        {
            counts[item] = counts.GetValueOrDefault(item) + 1;
        }

        foreach (var delivered in order.Items_delivered)
        {
            if (counts.GetValueOrDefault(delivered) <= 0)
            {
                continue;
            }

            counts[delivered]--;
        }

        return counts;
    }

    private static int CountMatchingInventory(IEnumerable<string> inventory, Dictionary<string, int> neededCounts)
    {
        var remaining = new Dictionary<string, int>(neededCounts, StringComparer.Ordinal);
        var matches = 0;

        foreach (var carried in inventory)
        {
            if (remaining.GetValueOrDefault(carried) <= 0)
            {
                continue;
            }

            remaining[carried]--;
            matches++;
        }

        return matches;
    }

    private static void ConsumeMatchingInventory(IEnumerable<string> inventory, Dictionary<string, int> neededCounts)
    {
        foreach (var carried in inventory)
        {
            if (neededCounts.GetValueOrDefault(carried) <= 0)
            {
                continue;
            }

            neededCounts[carried]--;
        }
    }

    private static PickupCandidate? FindBestPickupCandidate(
        Bot bot,
        IEnumerable<Item> items,
        Dictionary<string, int> demand,
        GameState state,
        List<int[]> deliveryTargets,
        HashSet<string> excludedItemIds,
        bool activeOrderPickup,
        string? preferredItemType = null,
        HashSet<(int X, int Y)>? excludedTargets = null)
    {
        PickupCandidate? best = null;

        foreach (var item in items)
        {
            if (excludedItemIds.Contains(item.Id) || demand.GetValueOrDefault(item.Type) <= 0)
            {
                continue;
            }

            var accessTarget = FindAdjacentWalkableTarget(item.Position, state, [], [], bot.Position);
            if (accessTarget == null)
            {
                continue;
            }

            if (excludedTargets != null && excludedTargets.Contains((accessTarget[0], accessTarget[1])))
            {
                continue;
            }

            var pickupDistance = GetPathDistance(bot.Position, accessTarget, state);
            if (pickupDistance == int.MaxValue)
            {
                continue;
            }

            var deliveryTarget = GetBestTarget(accessTarget, deliveryTargets, state);
            var deliveryDistance = deliveryTarget.Length >= 2
                ? GetPathDistance(accessTarget, deliveryTarget, state)
                : 0;
            if (deliveryTarget.Length >= 2 && deliveryDistance == int.MaxValue)
            {
                continue;
            }

            var loadBonus = (3 - bot.Inventory.Count) * (activeOrderPickup ? 2 : 1);
            var carryBonus = activeOrderPickup && bot.Inventory.Count > 0 ? 4 : 0;
            var preferredTypeBonus = !string.IsNullOrEmpty(preferredItemType) &&
                                     string.Equals(item.Type, preferredItemType, StringComparison.Ordinal)
                ? 6
                : 0;
            var score = (pickupDistance * 3) + deliveryDistance - loadBonus - carryBonus - preferredTypeBonus;

            if (best != null && score >= best.Score)
            {
                continue;
            }

            best = new PickupCandidate
            {
                Item = item,
                AccessTarget = accessTarget,
                PickupDistance = pickupDistance,
                DeliveryDistance = deliveryDistance,
                Score = score
            };
        }

        return best;
    }
}
