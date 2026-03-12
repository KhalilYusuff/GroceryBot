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

        return actions
            .OrderBy(action => action.BotId)
            .ToArray();
    }

    private static Dictionary<int, BotTask> AllocateTasks(
        GameState state,
        List<Bot> planningOrder,
        List<int[]> deliveryTargets)
    {
        var tasksByBotId = new Dictionary<int, BotTask>();
        var active = state.Orders.FirstOrDefault(o => o.Status == "active");
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

        if (largeTeam && availableBots.Count > 0 && activeDemand.Values.Any(count => count > 0))
        {
            var supportAssignments = AssignActiveSupportTasks(
                availableBots,
                state,
                activeDemand,
                deliveryTargets,
                tasksByBotId.Values.ToList(),
                Math.Min(3, availableBots.Count));
            foreach (var pair in supportAssignments)
            {
                tasksByBotId[pair.Key] = pair.Value;
                availableBots.RemoveAll(bot => bot.Id == pair.Key);
            }
        }

        var activeCovered = activeDemand.Values.All(count => count <= 0);
        if (!smallTeam && !recoveryMode && preview != null && previewPickupCap > 0)
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
            tasksByBotId[bot.Id] = new BotTask
            {
                Kind = largeTeam ? "park" : "stage",
                Target = largeTeam
                    ? GetBestParkingTarget(bot, state, deliveryTargets, assignedTasks)
                    : GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, endgame)
            };
            assignedTasks.Add(tasksByBotId[bot.Id]);
        }

        return tasksByBotId;
    }

    private static Dictionary<int, BotTask> AssignPickupTasks(
        List<Bot> candidateBots,
        GameState state,
        Order order,
        Dictionary<string, int> demand,
        List<int[]> deliveryTargets,
        string taskKind,
        int maxAssignments = int.MaxValue)
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
                    taskKind == "pickup_active");
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
        int maxAssignments)
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

            var target = GetBestActiveSupportTarget(bot, state, activeDemand, deliveryTargets, reservedTargets);
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
        List<int[]> deliveryTargets)
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
            true);
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
            Console.WriteLine($"fallback: mapped drop_off_zones={state.Drop_off_zones.Count}");
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
        bool activeOrderPickup)
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
            var score = (pickupDistance * 3) + deliveryDistance - loadBonus - carryBonus;

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
