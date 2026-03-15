namespace GroceryBot;

public partial class GroceryBotClient
{
    private static void UpdateProgressTracking(GameState state)
    {
        if (RecoveryRoundsRemaining > 0)
        {
            RecoveryRoundsRemaining--;
        }

        var activeBotIds = state.Bots.Select(bot => bot.Id).ToHashSet();
        foreach (var botId in LastActionAttempts.Keys.Where(botId => !activeBotIds.Contains(botId)).ToList())
        {
            LastActionAttempts.Remove(botId);
        }

        foreach (var botId in LastPlannedMoves.Keys.Where(botId => !activeBotIds.Contains(botId)).ToList())
        {
            LastPlannedMoves.Remove(botId);
        }

        foreach (var botId in CourierRoutes.Keys.Where(botId => !activeBotIds.Contains(botId)).ToList())
        {
            CourierRoutes.Remove(botId);
        }

        foreach (var botId in PickupAccessTargets.Keys.Where(botId => !activeBotIds.Contains(botId)).ToList())
        {
            PickupAccessTargets.Remove(botId);
        }

        foreach (var botId in RoleAssignments.Keys.Where(botId => !activeBotIds.Contains(botId)).ToList())
        {
            RoleAssignments.Remove(botId);
        }

        foreach (var botId in LastFinishAssignments.Keys.Where(botId => !activeBotIds.Contains(botId)).ToList())
        {
            LastFinishAssignments.Remove(botId);
        }

        foreach (var bot in state.Bots.Where(bot => bot.Position.Length >= 2))
        {
            var currentPosition = (bot.Position[0], bot.Position[1]);
            if (LastPositions.TryGetValue(bot.Id, out var lastPosition) && lastPosition == currentPosition)
            {
                StuckRounds[bot.Id] = StuckRounds.GetValueOrDefault(bot.Id) + 1;
            }
            else
            {
                StuckRounds[bot.Id] = 0;
            }

            LastPositions[bot.Id] = currentPosition;
        }
    }

    private static void LogMoveOutcomes(GameState state)
    {
        if (!DebugLogging)
        {
            return;
        }

        foreach (var bot in state.Bots.Where(bot => bot.Position.Length >= 2))
        {
            if (!LastPlannedMoves.TryGetValue(bot.Id, out var plannedMove))
            {
                continue;
            }

            var actual = (X: bot.Position[0], Y: bot.Position[1]);
            var matched = actual.X == plannedMove.X && actual.Y == plannedMove.Y;
            LogLine(
                $"result: bot={bot.Id} planned={plannedMove.Action} expected=[{plannedMove.X}, {plannedMove.Y}] actual=[{actual.X}, {actual.Y}] moved={matched}");
        }
    }

    private static void LogStateDelta(GameState? previous, GameState current, string? previousPayload)
    {
        if (!DebugLogging)
        {
            return;
        }

        if (previous == null || previous.Type != "game_state" || current.Type != "game_state")
        {
            return;
        }

        if (!string.IsNullOrEmpty(previousPayload))
        {
            LogLine($"prev_payload: {previousPayload}");
        }

        TrackActiveOrderTurnover(current);

        if (current.Score != previous.Score)
        {
            LogLine($"delta: score {previous.Score}->{current.Score}");
        }

        var previousBots = previous.Bots.ToDictionary(bot => bot.Id);
        foreach (var bot in current.Bots)
        {
            if (!previousBots.TryGetValue(bot.Id, out var previousBot))
            {
                continue;
            }

            var previousInventory = string.Join(",", previousBot.Inventory);
            var currentInventory = string.Join(",", bot.Inventory);
            if (!string.Equals(previousInventory, currentInventory, StringComparison.Ordinal))
            {
                LogLine($"delta: bot={bot.Id} inventory [{previousInventory}] -> [{currentInventory}]");
            }

            if (previousBot.Position.Length >= 2 && bot.Position.Length >= 2 &&
                (previousBot.Position[0] != bot.Position[0] || previousBot.Position[1] != bot.Position[1]))
            {
                LogLine(
                    $"delta: bot={bot.Id} position [{previousBot.Position[0]}, {previousBot.Position[1]}] -> [{bot.Position[0]}, {bot.Position[1]}]");
            }
        }

        var previousItemIds = previous.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var currentItemIds = current.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removedItemId in previousItemIds.Except(currentItemIds).OrderBy(id => id))
        {
            LogLine($"delta: item_removed {removedItemId}");
        }

        var previousOrders = previous.Orders.ToDictionary(order => order.Id);
        foreach (var order in current.Orders)
        {
            if (!previousOrders.TryGetValue(order.Id, out var previousOrder))
            {
                continue;
            }

            var previousDelivered = string.Join(",", previousOrder.Items_delivered);
            var currentDelivered = string.Join(",", order.Items_delivered);
            if (!string.Equals(previousDelivered, currentDelivered, StringComparison.Ordinal))
            {
                LogLine($"delta: order={order.Id} delivered [{previousDelivered}] -> [{currentDelivered}]");
            }

            if (!string.Equals(previousOrder.Status, order.Status, StringComparison.Ordinal) ||
                previousOrder.Complete != order.Complete)
            {
                LogLine(
                    $"delta: order={order.Id} status {previousOrder.Status}/{previousOrder.Complete} -> {order.Status}/{order.Complete}");
            }
        }

        UpdateActionFeedback(previous, current);
    }

    private static void UpdateActionFeedback(GameState previous, GameState current)
    {
        var previousBots = previous.Bots.ToDictionary(bot => bot.Id);
        var currentItems = current.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var previousDeliveredByOrder = previous.Orders.ToDictionary(
            order => order.Id,
            order => string.Join(",", order.Items_delivered),
            StringComparer.Ordinal);

        foreach (var bot in current.Bots)
        {
            if (!LastActionAttempts.TryGetValue(bot.Id, out var attempt) ||
                !previousBots.TryGetValue(bot.Id, out var previousBot))
            {
                continue;
            }

            var inventoryChanged = string.Join(",", previousBot.Inventory) != string.Join(",", bot.Inventory);
            switch (attempt.Action)
            {
                case "drop_off":
                {
                    var ordersChanged = current.Orders.Any(order =>
                        previousDeliveredByOrder.TryGetValue(order.Id, out var previousDelivered) &&
                        previousDelivered != string.Join(",", order.Items_delivered));
                    if (!inventoryChanged && !ordersChanged && current.Score == previous.Score)
                    {
                        RecentDropoffNoEffectCount++;
                        LogDebug($"feedback: bot={bot.Id} drop_off_no_effect");
                    }

                    break;
                }
                case "pick_up":
                {
                    var itemRemoved = !string.IsNullOrEmpty(attempt.ItemId) && !currentItems.Contains(attempt.ItemId);
                    if (!inventoryChanged && !itemRemoved)
                    {
                        RecentPickupNoEffectCount++;
                        LogDebug($"feedback: bot={bot.Id} pick_up_no_effect item={attempt.ItemId}");
                    }

                    break;
                }
            }
        }
    }

    private static void LogDebug(string message)
    {
        if (!DebugLogging)
        {
            return;
        }

        LogLine(message);
    }

    private static void MaybeLogRoundSummary(GameState state)
    {
        if (state.Round == 0)
        {
            TrackActiveOrderTurnover(state);
            LastObservedScore = state.Score;
            RecentPickupNoEffectCount = 0;
            RecentDropoffNoEffectCount = 0;
            RecentFallbackPickupAttempts = 0;
            RecentFallbackPickupSuccesses = 0;
            RecentFallbackPickupNoCandidate = 0;
            return;
        }

        if (state.Round % SummaryIntervalRounds != 0 &&
            state.Round != state.Max_rounds - 1)
        {
            return;
        }

        var active = state.Orders.FirstOrDefault(order => order.Status == "active");
        var preview = state.Orders.FirstOrDefault(order => order.Status == "preview");
        var deliveryTargets = GetDeliveryTargets(state);
        var activeOutstanding = active == null ? 0 : GetOutstandingCounts(active).Values.Where(count => count > 0).Sum();
        var previewOutstanding = preview == null ? 0 : GetOutstandingCounts(preview).Values.Where(count => count > 0).Sum();
        var activeDemand = active == null ? new Dictionary<string, int>() : GetOutstandingCounts(active);
        var previewDemand = preview == null ? new Dictionary<string, int>() : GetOutstandingCounts(preview);
        var carryingActive = state.Bots.Count(bot => CountReliableMatchingInventory(bot, state, activeDemand, deliveryTargets) > 0);
        var carryingPreview = state.Bots.Count(bot => CountReliableMatchingInventory(bot, state, previewDemand, deliveryTargets) > 0);
        TrackActiveOrderTurnover(state);
        var activeOrderId = active?.Id ?? "-";
        var scoreDelta = state.Score - LastObservedScore;
        LastActiveDemandSummary = string.Join(
            ",",
            activeDemand
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        LastActiveWorldSummary = string.Join(
            ",",
            activeDemand
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{state.Items.Count(item => string.Equals(item.Type, pair.Key, StringComparison.Ordinal))}"));
        LastFallbackPickupSummary =
            $"attempts={RecentFallbackPickupAttempts} successes={RecentFallbackPickupSuccesses} no_candidate={RecentFallbackPickupNoCandidate}";

        LogLine(
            $"round_summary: planner={PlannerVersion} round={state.Round} score={state.Score} score_delta={scoreDelta} active_order_id={activeOrderId} active_order_turnovers={ActiveOrderTurnovers} active_outstanding={activeOutstanding} preview_outstanding={previewOutstanding} " +
            $"carry_active={carryingActive} carry_preview={carryingPreview} pickup_no_effect={RecentPickupNoEffectCount} dropoff_no_effect={RecentDropoffNoEffectCount} " +
            $"roles=({LastRoleSummary ?? "none"}) tasks=({LastTaskSummary ?? "none"})");
        if (!string.IsNullOrEmpty(LastActiveDemandSummary))
        {
            LogLine($"active_demand: {LastActiveDemandSummary}");
            LogLine($"active_world: {LastActiveWorldSummary}");
            LogLine($"fallback_pickups: {LastFallbackPickupSummary}");
        }

        if (!string.IsNullOrEmpty(LastDeliveryPressureSummary))
        {
            LogLine($"delivery_pressure: {LastDeliveryPressureSummary}");
        }

        if (!string.IsNullOrEmpty(LastFutureDemandSummary))
        {
            LogLine($"future_demand: {LastFutureDemandSummary}");
        }

        if (!string.IsNullOrEmpty(LastKnownNextOrdersSummary))
        {
            LogLine($"known_next_orders: {LastKnownNextOrdersSummary}");
        }

        if (!string.IsNullOrEmpty(LastPlannedTimelineSummary))
        {
            LogLine($"planned_timeline: {LastPlannedTimelineSummary}");
        }

        if (!string.IsNullOrEmpty(LastPrefetchBudgetSummary))
        {
            LogLine($"prefetch_budget: {LastPrefetchBudgetSummary}");
        }

        if (!string.IsNullOrEmpty(LastDayScriptConfidence))
        {
            LogLine($"day_script_confidence: {LastDayScriptConfidence}");
        }

        LastObservedScore = state.Score;
        RecentPickupNoEffectCount = 0;
        RecentDropoffNoEffectCount = 0;
        RecentFallbackPickupAttempts = 0;
        RecentFallbackPickupSuccesses = 0;
        RecentFallbackPickupNoCandidate = 0;
    }

    private static void TrackActiveOrderTurnover(GameState state)
    {
        var activeOrderId = state.Orders
            .FirstOrDefault(order => string.Equals(order.Status, "active", StringComparison.Ordinal))?
            .Id;
        if (string.IsNullOrEmpty(activeOrderId))
        {
            return;
        }

        if (string.IsNullOrEmpty(LastTrackedActiveOrderId))
        {
            LastTrackedActiveOrderId = activeOrderId;
            return;
        }

        if (!string.Equals(LastTrackedActiveOrderId, activeOrderId, StringComparison.Ordinal))
        {
            ActiveOrderTurnovers++;
            LastTrackedActiveOrderId = activeOrderId;
        }
    }

    private static void LogFinishModeAssignments(
        GameState state,
        Dictionary<int, BotTask> tasksByBotId,
        Dictionary<int, string> rolesByBotId,
        Dictionary<string, int> finishDemandSnapshot)
    {
        var activeTypes = finishDemandSnapshot
            .Where(pair => pair.Value > 0)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (activeTypes.Count == 0)
        {
            LastFinishAssignments.Clear();
            return;
        }

        var trackedBotIds = new HashSet<int>();
        foreach (var bot in state.Bots.OrderBy(bot => bot.Id))
        {
            var task = tasksByBotId.GetValueOrDefault(bot.Id);
            var carriesActive = CountReliableMatchingInventory(bot, state, finishDemandSnapshot, GetDeliveryTargets(state)) > 0;
            var shouldLog = task?.Kind == "pickup_active" ||
                            (task?.Kind == "deliver" && carriesActive);
            if (!shouldLog)
            {
                continue;
            }

            trackedBotIds.Add(bot.Id);
            var target = task?.Target;
            var blockedByBot = target != null && target.Length >= 2
                ? state.Bots.FirstOrDefault(other =>
                    other.Id != bot.Id &&
                    other.Position.Length >= 2 &&
                    other.Position[0] == target[0] &&
                    other.Position[1] == target[1])?.Id
                : null;
            var assignmentRounds = UpdateFinishAssignmentSnapshot(bot.Id, task);
            LogLine(
                $"finish_trace: round={state.Round} bot={bot.Id} role={rolesByBotId.GetValueOrDefault(bot.Id, "reserve")} task={task?.Kind ?? "none"} " +
                $"item={task?.ItemId ?? "-"} type={task?.ItemType ?? "-"} target={FormatPoint(target ?? [])} pos={FormatPoint(bot.Position)} " +
                $"stuck={StuckRounds.GetValueOrDefault(bot.Id)} same_assignment_rounds={assignmentRounds} target_blocked_by={(blockedByBot?.ToString() ?? "none")} " +
                $"inventory=[{string.Join(",", bot.Inventory)}]");
        }

        foreach (var botId in LastFinishAssignments.Keys.Where(botId => !trackedBotIds.Contains(botId)).ToList())
        {
            LastFinishAssignments.Remove(botId);
        }
    }

    private static int UpdateFinishAssignmentSnapshot(int botId, BotTask? task)
    {
        if (task == null)
        {
            LastFinishAssignments.Remove(botId);
            return 0;
        }

        var currentTarget = task.Target;
        var sameAsPrevious =
            LastFinishAssignments.TryGetValue(botId, out var previous) &&
            previous.TaskKind == task.Kind &&
            string.Equals(previous.ItemId, task.ItemId, StringComparison.Ordinal) &&
            string.Equals(previous.ItemType, task.ItemType, StringComparison.Ordinal) &&
            TargetsEqual(previous.Target, currentTarget);

        var rounds = sameAsPrevious ? previous!.ConsecutiveRounds + 1 : 1;
        LastFinishAssignments[botId] = new FinishAssignmentSnapshot
        {
            TaskKind = task.Kind,
            ItemId = task.ItemId,
            ItemType = task.ItemType,
            Target = currentTarget,
            ConsecutiveRounds = rounds
        };
        return rounds;
    }

    private static bool TargetsEqual(int[]? left, int[]? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }

        return left.Length >= 2 &&
               right.Length >= 2 &&
               left[0] == right[0] &&
               left[1] == right[1];
    }
}
