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
        var activeOutstanding = active == null ? 0 : GetOutstandingCounts(active).Values.Where(count => count > 0).Sum();
        var previewOutstanding = preview == null ? 0 : GetOutstandingCounts(preview).Values.Where(count => count > 0).Sum();
        var activeDemand = active == null ? new Dictionary<string, int>() : GetOutstandingCounts(active);
        var previewDemand = preview == null ? new Dictionary<string, int>() : GetOutstandingCounts(preview);
        var carryingActive = state.Bots.Count(bot => CountMatchingInventory(bot.Inventory, activeDemand) > 0);
        var carryingPreview = state.Bots.Count(bot => CountMatchingInventory(bot.Inventory, previewDemand) > 0);
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
            $"round_summary: planner={PlannerVersion} round={state.Round} score={state.Score} score_delta={scoreDelta} active_outstanding={activeOutstanding} preview_outstanding={previewOutstanding} " +
            $"carry_active={carryingActive} carry_preview={carryingPreview} pickup_no_effect={RecentPickupNoEffectCount} dropoff_no_effect={RecentDropoffNoEffectCount} " +
            $"roles=({LastRoleSummary ?? "none"}) tasks=({LastTaskSummary ?? "none"})");
        if (!string.IsNullOrEmpty(LastActiveDemandSummary))
        {
            LogLine($"active_demand: {LastActiveDemandSummary}");
            LogLine($"active_world: {LastActiveWorldSummary}");
            LogLine($"fallback_pickups: {LastFallbackPickupSummary}");
        }

        LastObservedScore = state.Score;
        RecentPickupNoEffectCount = 0;
        RecentDropoffNoEffectCount = 0;
        RecentFallbackPickupAttempts = 0;
        RecentFallbackPickupSuccesses = 0;
        RecentFallbackPickupNoCandidate = 0;
    }
}
