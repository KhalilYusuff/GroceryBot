using GroceryBot.Model;
using System.Text.Json;

namespace GroceryBot;

public partial class GroceryBotClient
{
    private static BotResponse MoveToward(Bot bot, GameState state, int[] target, HashSet<(int X, int Y)> occupiedPositions)
    {
        var nextAction = FindPathAction(bot.Position, target, state, occupiedPositions, [], false);
        return CreateResponse(bot.Id, nextAction);
    }

    private static BotResponse MoveToward(
        Bot bot,
        GameState state,
        int[] target,
        HashSet<(int X, int Y)> occupiedPositions,
        HashSet<(int X, int Y)> reservedCorridorCells,
        bool prioritizeCourier)
    {
        var nextAction = FindPathAction(bot.Position, target, state, occupiedPositions, reservedCorridorCells, prioritizeCourier);
        return CreateResponse(bot.Id, nextAction);
    }

    private static BotResponse ExecuteDeliverMoveTask(
        Bot bot,
        GameState state,
        PlanningContext planningContext,
        int[] deliveryTarget,
        string role)
    {
        var route = GetCourierRoute(bot, deliveryTarget, state);
        var preferredNode = GetNextCourierNode(bot, route);
        if (preferredNode != null)
        {
            var preferredTarget = new[] { preferredNode.Value.X, preferredNode.Value.Y };
            var preferredAction = ToAction((bot.Position[0], bot.Position[1]), preferredNode.Value);
            if (preferredAction != "wait" &&
                IsWalkable(preferredTarget, state, planningContext.OccupiedPositions))
            {
                LogMoveDiagnostics(bot, state, preferredTarget, preferredAction, $"{role}:route");
                return CreateResponse(bot.Id, preferredAction);
            }
        }

        var response = ExecuteMoveTask(bot, state, planningContext, deliveryTarget, true, role);
        if (response.Actions[0].Action == "wait")
        {
            CourierRoutes.Remove(bot.Id);
        }

        return response;
    }

    private static BotResponse ExecuteMoveTask(
        Bot bot,
        GameState state,
        PlanningContext planningContext,
        int[] target,
        bool prioritizeCourier,
        string role)
    {
        var response = MoveToward(
            bot,
            state,
            target,
            planningContext.OccupiedPositions,
            planningContext.ReservedCorridorCells,
            prioritizeCourier);
        LogMoveDiagnostics(bot, state, target, response.Actions[0].Action, role);
        return response;
    }

    private static void LogMoveDiagnostics(Bot bot, GameState state, int[] target, string action, string role)
    {
        if (!DebugLogging)
        {
            return;
        }

        var next = GetNextPosition(bot.Position, action);
        var roundCache = GetRoundCache(state);
        var isWall = next.Length >= 2 && roundCache.WallCells.Contains((next[0], next[1]));
        var isShelf = next.Length >= 2 && roundCache.ShelfCells.Contains((next[0], next[1]));
        var blockingBot = state.Bots.FirstOrDefault(other =>
            other.Id != bot.Id &&
            other.Position.Length >= 2 &&
            other.Position[0] == next[0] &&
            other.Position[1] == next[1]);

        Console.WriteLine(
            $"diag: bot={bot.Id} role={role} current={FormatPoint(bot.Position)} target={FormatPoint(target)} " +
            $"action={action} next={FormatPoint(next)} wall={isWall} shelf={isShelf} " +
            $"blocked_by_bot={(blockingBot == null ? "none" : blockingBot.Id)}");
    }

    private static int[] GetNextPosition(int[] current, string action)
    {
        if (current.Length < 2)
        {
            return [];
        }

        return action switch
        {
            "move_up" => [current[0], current[1] - 1],
            "move_down" => [current[0], current[1] + 1],
            "move_left" => [current[0] - 1, current[1]],
            "move_right" => [current[0] + 1, current[1]],
            _ => [current[0], current[1]]
        };
    }

    private static string FormatPoint(int[] point)
    {
        return point.Length >= 2 ? $"[{point[0]}, {point[1]}]" : "[]";
    }

    private static int GetHeuristicDistance(int[] start, int[] target)
    {
        if (start.Length < 2 || target.Length < 2)
        {
            return int.MaxValue;
        }

        return Math.Abs(start[0] - target[0]) + Math.Abs(start[1] - target[1]);
    }

    private static List<int[]> GetDeliveryTargets(GameState state)
    {
        if (state.Drop_off_zones.Count > 0)
        {
            return state.Drop_off_zones.Where(zone => zone.Length >= 2).ToList();
        }

        if (state.Drop_off.Length >= 2)
        {
            return [state.Drop_off];
        }

        return [];
    }

    private static bool IsAtAnyTarget(int[] position, IEnumerable<int[]> targets)
    {
        if (position.Length < 2)
        {
            return false;
        }

        return targets.Any(target => target.Length >= 2 && position[0] == target[0] && position[1] == target[1]);
    }

    private static int[] GetBestTarget(int[] start, List<int[]> targets, GameState state)
    {
        if (targets.Count == 0)
        {
            return [];
        }

        return targets
            .OrderBy(target => GetPathDistance(start, target, state))
            .ThenBy(target => Math.Abs(start[0] - target[0]) + Math.Abs(start[1] - target[1]))
            .First();
    }

    private static int[] SelectDeliveryTarget(
        Bot bot,
        GameState state,
        List<int[]> deliveryTargets,
        List<BotTask> assignedTasks)
    {
        if (deliveryTargets.Count <= 1)
        {
            var singleTarget = deliveryTargets.FirstOrDefault() ?? [];
            if (singleTarget.Length >= 2)
            {
                CourierTargets[bot.Id] = singleTarget;
            }

            return singleTarget;
        }

        if (CourierTargets.TryGetValue(bot.Id, out var existingTarget) &&
            existingTarget.Length >= 2 &&
            deliveryTargets.Any(target => target.Length >= 2 && target[0] == existingTarget[0] && target[1] == existingTarget[1]))
        {
            return existingTarget;
        }

        var assignedCounts = assignedTasks
            .Where(task => task.Kind == "deliver" && task.Target != null && task.Target.Length >= 2)
            .GroupBy(task => (task.Target![0], task.Target[1]))
            .ToDictionary(group => group.Key, group => group.Count());

        var selected = deliveryTargets
            .Where(target => target.Length >= 2)
            .OrderBy(target => assignedCounts.GetValueOrDefault((target[0], target[1])))
            .ThenBy(target => GetHeuristicDistance(bot.Position, target))
            .ThenBy(target => GetPathDistance(bot.Position, target, state))
            .FirstOrDefault() ?? [];

        if (selected.Length >= 2)
        {
            CourierTargets[bot.Id] = selected;
        }

        return selected;
    }

    private static int[] GetBestStageTarget(
        Bot bot,
        GameState state,
        List<int[]> deliveryTargets,
        List<BotTask> assignedTasks,
        bool endgame)
    {
        var reservedStageTargets = assignedTasks
            .Where(task => task.Kind == "stage" && task.Target != null && task.Target.Length >= 2)
            .Select(task => (task.Target![0], task.Target[1]))
            .ToHashSet();

        var candidates = new List<int[]>();
        var preferredRows = endgame
            ? new[] { 1, state.Grid.Height - 2, 2, state.Grid.Height - 3 }
            : new[] { 1, state.Grid.Height - 2, 2, state.Grid.Height - 3, 3, state.Grid.Height - 4 };
        foreach (var y in preferredRows.Distinct().Where(y => y > 0 && y < state.Grid.Height - 1))
        {
            for (var x = 1; x < state.Grid.Width - 1; x++)
            {
                var point = new[] { x, y };
                if (!IsStageCell(point, state, deliveryTargets) || reservedStageTargets.Contains((x, y)))
                {
                    continue;
                }

                candidates.Add(point);
            }
        }

        if (candidates.Count == 0)
        {
            return deliveryTargets.FirstOrDefault() ?? [];
        }

        return candidates
            .OrderByDescending(candidate => GetPerimeterScore(candidate, state))
            .ThenByDescending(candidate => deliveryTargets.Count == 0
                ? 0
                : deliveryTargets.Min(target => GetHeuristicDistance(candidate, target)))
            .ThenBy(candidate => GetPathDistance(bot.Position, candidate, state))
            .ThenBy(candidate => candidate[1])
            .ThenBy(candidate => candidate[0])
            .First();
    }

    private static int GetPerimeterScore(int[] candidate, GameState state)
    {
        if (candidate.Length < 2)
        {
            return int.MinValue;
        }

        var left = candidate[0];
        var right = (state.Grid.Width - 1) - candidate[0];
        var top = candidate[1];
        var bottom = (state.Grid.Height - 1) - candidate[1];
        return Math.Min(Math.Min(left, right), Math.Min(top, bottom)) switch
        {
            1 => 4,
            2 => 3,
            3 => 2,
            _ => 1
        };
    }

    private static int[] GetBestPreviewHoldTarget(
        Bot bot,
        GameState state,
        List<int[]> deliveryTargets,
        List<BotTask> assignedTasks)
    {
        var reservedTargets = assignedTasks
            .Where(task => task.Kind == "hold_preview" && task.Target != null && task.Target.Length >= 2)
            .Select(task => (task.Target![0], task.Target[1]))
            .ToHashSet();

        var candidates = new List<int[]>();
        foreach (var zone in deliveryTargets.Where(target => target.Length >= 2))
        {
            candidates.Add(zone);

            foreach (var adjacent in GetAdjacentTargets(zone))
            {
                if (adjacent.Length < 2 || IsStaticBlocked(adjacent[0], adjacent[1], state))
                {
                    continue;
                }

                candidates.Add(adjacent);
            }

            foreach (var secondRing in GetNeighbors((zone[0], zone[1]))
                         .SelectMany(GetNeighbors)
                         .Distinct())
            {
                var point = new[] { secondRing.X, secondRing.Y };
                if (point[0] < 0 || point[0] >= state.Grid.Width || point[1] < 0 || point[1] >= state.Grid.Height)
                {
                    continue;
                }

                if (IsStaticBlocked(point[0], point[1], state))
                {
                    continue;
                }

                candidates.Add(point);
            }
        }

        var selected = candidates
            .Where(candidate => candidate.Length >= 2)
            .Where(candidate => !reservedTargets.Contains((candidate[0], candidate[1])))
            .DistinctBy(candidate => (candidate[0], candidate[1]))
            .OrderBy(candidate => deliveryTargets.Min(zone => GetHeuristicDistance(candidate, zone)))
            .ThenBy(candidate => GetPathDistance(bot.Position, candidate, state))
            .ThenBy(candidate => candidate[1])
            .ThenBy(candidate => candidate[0])
            .FirstOrDefault();

        return selected ?? GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, false);
    }

    private static int[] GetBestParkingTarget(
        Bot bot,
        GameState state,
        List<int[]> deliveryTargets,
        List<BotTask> assignedTasks)
    {
        if (ParkingTargets.TryGetValue(bot.Id, out var existingTarget) &&
            existingTarget.Length >= 2 &&
            !IsStaticBlocked(existingTarget[0], existingTarget[1], state))
        {
            return existingTarget;
        }

        var reservedTargets = assignedTasks
            .Where(task => task.Kind == "park" && task.Target != null && task.Target.Length >= 2)
            .Select(task => (task.Target![0], task.Target[1]))
            .ToHashSet();

        var perimeterCells = new List<int[]>();
        for (var x = 1; x < state.Grid.Width - 1; x++)
        {
            perimeterCells.Add([x, 1]);
            perimeterCells.Add([x, state.Grid.Height - 2]);
        }

        for (var y = 2; y < state.Grid.Height - 2; y++)
        {
            perimeterCells.Add([1, y]);
            perimeterCells.Add([state.Grid.Width - 2, y]);
        }

        var selected = perimeterCells
            .Where(cell => !reservedTargets.Contains((cell[0], cell[1])))
            .Where(cell => !IsStaticBlocked(cell[0], cell[1], state))
            .Where(cell => !deliveryTargets.Any(zone => zone.Length >= 2 && GetHeuristicDistance(cell, zone) <= 2))
            .DistinctBy(cell => (cell[0], cell[1]))
            .OrderByDescending(cell => deliveryTargets.Count == 0
                ? 0
                : deliveryTargets.Min(zone => GetHeuristicDistance(cell, zone)))
            .ThenBy(cell => ((cell[0] * 31) + (cell[1] * 17) + (bot.Id * 13)) % 23)
            .ThenBy(cell => GetPathDistance(bot.Position, cell, state))
            .FirstOrDefault();

        var fallback = selected ?? GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, false);
        if (fallback.Length >= 2)
        {
            ParkingTargets[bot.Id] = fallback;
        }

        return fallback;
    }

    private static int[]? GetBestActiveSupportTarget(
        Bot bot,
        GameState state,
        Dictionary<string, int> activeDemand,
        List<int[]> deliveryTargets,
        HashSet<(int X, int Y)> reservedTargets)
    {
        return state.Items
            .Where(item => activeDemand.GetValueOrDefault(item.Type) > 0)
            .Select(item => new
            {
                Target = FindAdjacentWalkableTarget(item.Position, state, [], reservedTargets, bot.Position),
                DeliveryDistance = deliveryTargets.Count == 0
                    ? 0
                    : deliveryTargets.Min(zone => zone.Length >= 2
                        ? GetPathDistance(item.Position, zone, state)
                        : int.MaxValue)
            })
            .Where(candidate => candidate.Target != null)
            .OrderBy(candidate => GetPathDistance(bot.Position, candidate.Target!, state))
            .ThenBy(candidate => candidate.DeliveryDistance)
            .Select(candidate => candidate.Target)
            .FirstOrDefault();
    }

    private static bool IsStageCell(int[] position, GameState state, List<int[]> deliveryTargets)
    {
        if (position.Length < 2)
        {
            return false;
        }

        if (position[0] <= 0 || position[0] >= state.Grid.Width - 1 || position[1] <= 0 || position[1] >= state.Grid.Height - 1)
        {
            return false;
        }

        if (deliveryTargets.Any(target => target.Length >= 2 && target[0] == position[0] && target[1] == position[1]))
        {
            return false;
        }

        if (deliveryTargets.Any(target => target.Length >= 2 &&
            Math.Abs(target[0] - position[0]) + Math.Abs(target[1] - position[1]) <= 3))
        {
            return false;
        }

        return !IsStaticBlocked(position[0], position[1], state);
    }

    private static int[]? FindAdjacentWalkableTarget(
        int[] itemPosition,
        GameState state,
        HashSet<(int X, int Y)> occupiedPositions,
        HashSet<(int X, int Y)> reservedTargets,
        int[] botPosition)
    {
        if (itemPosition.Length < 2)
        {
            return null;
        }

        var candidates = new[]
        {
            new[] { itemPosition[0], itemPosition[1] - 1 },
            new[] { itemPosition[0], itemPosition[1] + 1 },
            new[] { itemPosition[0] - 1, itemPosition[1] },
            new[] { itemPosition[0] + 1, itemPosition[1] }
        };

        return candidates
            .Where(candidate => IsWalkable(candidate, state, occupiedPositions))
            .Where(candidate => !reservedTargets.Contains((candidate[0], candidate[1])))
            .OrderBy(candidate => GetHeuristicDistance(botPosition, candidate))
            .FirstOrDefault();
    }

    private static bool IsWalkable(int[] position, GameState state, HashSet<(int X, int Y)> occupiedPositions)
    {
        if (position.Length < 2)
        {
            return false;
        }

        var x = position[0];
        var y = position[1];

        if (x < 0 || x >= state.Grid.Width || y < 0 || y >= state.Grid.Height)
        {
            return false;
        }

        if (IsStaticBlocked(x, y, state))
        {
            return false;
        }

        if (occupiedPositions.Contains((x, y)))
        {
            return false;
        }

        return true;
    }

    private static string FindPathAction(
        int[] start,
        int[] target,
        GameState state,
        HashSet<(int X, int Y)> occupiedPositions,
        HashSet<(int X, int Y)> reservedCorridorCells,
        bool prioritizeCourier)
    {
        if (start.Length < 2 || target.Length < 2)
        {
            return "wait";
        }

        if (start[0] == target[0] && start[1] == target[1])
        {
            return "wait";
        }

        var startNode = (X: start[0], Y: start[1]);
        var targetNode = (X: target[0], Y: target[1]);
        var queue = new Queue<(int X, int Y)>();
        var visited = new HashSet<(int X, int Y)> { startNode };
        var previous = new Dictionary<(int X, int Y), (int X, int Y)>();

        queue.Enqueue(startNode);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == targetNode)
            {
                break;
            }

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor) || !IsPathNodeWalkable(neighbor, state))
                {
                    continue;
                }

                if (!prioritizeCourier &&
                    reservedCorridorCells.Contains(neighbor) &&
                    !(neighbor.X == targetNode.X && neighbor.Y == targetNode.Y))
                {
                    continue;
                }

                visited.Add(neighbor);
                previous[neighbor] = current;
                queue.Enqueue(neighbor);
            }
        }

        if (!visited.Contains(targetNode))
        {
            return FindBestImmediateStep(start, target, state, occupiedPositions, reservedCorridorCells, prioritizeCourier) ?? "wait";
        }

        var step = targetNode;
        while (previous.TryGetValue(step, out var parent) && parent != startNode)
        {
            step = parent;
        }

        var nextAction = ToAction(startNode, step);
        var nextPosition = GetNextPosition(start, nextAction);
        if (nextPosition.Length >= 2 && occupiedPositions.Contains((nextPosition[0], nextPosition[1])))
        {
            return FindBestImmediateStep(start, target, state, occupiedPositions, reservedCorridorCells, prioritizeCourier) ?? "wait";
        }

        return nextAction;
    }

    private static int GetPathDistance(
        int[] start,
        int[] target,
        GameState state)
    {
        if (start.Length < 2 || target.Length < 2)
        {
            return int.MaxValue;
        }

        var roundCache = GetRoundCache(state);
        var key = (start[0], start[1], target[0], target[1]);
        if (roundCache.DistanceCache.TryGetValue(key, out var cachedDistance))
        {
            return cachedDistance;
        }

        if (roundCache.PathCache.TryGetValue(key, out var cachedPath))
        {
            var cachedPathDistance = cachedPath.Count == 0 ? int.MaxValue : cachedPath.Count - 1;
            roundCache.DistanceCache[key] = cachedPathDistance;
            return cachedPathDistance;
        }

        var path = ComputePathNodes(start, target, state);
        roundCache.PathCache[key] = path;
        var distance = path.Count == 0 ? int.MaxValue : path.Count - 1;
        roundCache.DistanceCache[key] = distance;
        return distance;
    }

    private static List<(int X, int Y)> GetPathNodes(
        int[] start,
        int[] target,
        GameState state)
    {
        if (start.Length < 2 || target.Length < 2)
        {
            return [];
        }

        var roundCache = GetRoundCache(state);
        var key = (start[0], start[1], target[0], target[1]);
        if (roundCache.PathCache.TryGetValue(key, out var cachedPath))
        {
            return cachedPath;
        }

        var path = ComputePathNodes(start, target, state);
        roundCache.PathCache[key] = path;
        roundCache.DistanceCache[key] = path.Count == 0 ? int.MaxValue : path.Count - 1;
        return path;
    }

    private static IEnumerable<(int X, int Y)> GetNeighbors((int X, int Y) point)
    {
        yield return (point.X, point.Y - 1);
        yield return (point.X + 1, point.Y);
        yield return (point.X, point.Y + 1);
        yield return (point.X - 1, point.Y);
    }

    private static bool IsPathNodeWalkable((int X, int Y) position, GameState state)
    {
        return position.X >= 0 &&
               position.X < state.Grid.Width &&
               position.Y >= 0 &&
               position.Y < state.Grid.Height &&
               !IsStaticBlocked(position.X, position.Y, state);
    }

    private static List<(int X, int Y)> ComputePathNodes(
        int[] start,
        int[] target,
        GameState state)
    {
        var startNode = (X: start[0], Y: start[1]);
        var targetNode = (X: target[0], Y: target[1]);
        var queue = new Queue<(int X, int Y)>();
        var visited = new HashSet<(int X, int Y)> { startNode };
        var previous = new Dictionary<(int X, int Y), (int X, int Y)>();

        queue.Enqueue(startNode);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == targetNode)
            {
                break;
            }

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor) || !IsPathNodeWalkable(neighbor, state))
                {
                    continue;
                }

                visited.Add(neighbor);
                previous[neighbor] = current;
                queue.Enqueue(neighbor);
            }
        }

        if (!visited.Contains(targetNode))
        {
            return [];
        }

        var path = new List<(int X, int Y)>();
        var step = targetNode;
        path.Add(step);

        while (previous.TryGetValue(step, out var parent))
        {
            path.Add(parent);
            if (parent == startNode)
            {
                break;
            }

            step = parent;
        }

        path.Reverse();
        return path;
    }

    private static RoundCache GetRoundCache(GameState state)
    {
        return CurrentRoundCache ??= CreateRoundCache(state);
    }

    private static RoundCache CreateRoundCache(GameState state)
    {
        foreach (var shelf in state.Items
                     .Where(item => item.Position.Length >= 2)
                     .Select(item => (item.Position[0], item.Position[1])))
        {
            KnownShelfCells.Add(shelf);
        }

        var wallCells = state.Grid.Walls
            .Where(wall => wall.Length >= 2)
            .Select(wall => (wall[0], wall[1]))
            .ToHashSet();
        var shelfCells = KnownShelfCells.ToHashSet();

        return new RoundCache
        {
            WallCells = wallCells,
            ShelfCells = shelfCells
        };
    }

    private static bool IsStaticBlocked(int x, int y, GameState state)
    {
        var roundCache = GetRoundCache(state);
        return roundCache.WallCells.Contains((x, y)) || roundCache.ShelfCells.Contains((x, y));
    }

    private static string FindRecoveryAction(
        Bot bot,
        GameState state,
        HashSet<(int X, int Y)> occupiedPositions,
        int[] preferredTarget)
    {
        var stuckRounds = StuckRounds.GetValueOrDefault(bot.Id);
        if (stuckRounds == 0)
        {
            return "wait";
        }

        var preferredStep = FindBestImmediateStep(bot.Position, preferredTarget, state, occupiedPositions, [], false);
        if (!string.IsNullOrEmpty(preferredStep))
        {
            return preferredStep;
        }

        if (bot.Position.Length < 2)
        {
            return "wait";
        }

        var current = (X: bot.Position[0], Y: bot.Position[1]);
        var shuffle = GetNeighbors(current)
            .Where(neighbor => IsWalkable([neighbor.X, neighbor.Y], state, occupiedPositions))
            .OrderBy(neighbor => CountAdjacentBots(neighbor, occupiedPositions))
            .ThenByDescending(neighbor => preferredTarget.Length < 2
                ? 0
                : Math.Abs(neighbor.X - preferredTarget[0]) + Math.Abs(neighbor.Y - preferredTarget[1]))
            .Select(neighbor => ToAction(current, neighbor))
            .FirstOrDefault();

        return shuffle ?? "wait";
    }

    private static int[]? GetPickupAccessTarget(
        Bot bot,
        Item item,
        GameState state,
        PlanningContext planningContext)
    {
        if (PickupAccessTargets.TryGetValue(bot.Id, out var accessTarget) &&
            accessTarget.ItemId == item.Id)
        {
            var cachedTarget = new[] { accessTarget.X, accessTarget.Y };
            if (IsAccessiblePickupTarget(bot, cachedTarget, state, planningContext))
            {
                return cachedTarget;
            }
        }

        var selected = GetAdjacentTargets(item.Position)
            .Where(candidate => IsAccessiblePickupTarget(bot, candidate, state, planningContext))
            .OrderBy(candidate => GetHeuristicDistance(bot.Position, candidate))
            .FirstOrDefault();

        if (selected == null)
        {
            PickupAccessTargets.Remove(bot.Id);
            return null;
        }

        PickupAccessTargets[bot.Id] = (item.Id, selected[0], selected[1]);
        return selected;
    }

    private static bool IsAccessiblePickupTarget(
        Bot bot,
        int[] target,
        GameState state,
        PlanningContext planningContext)
    {
        if (target.Length < 2)
        {
            return false;
        }

        if (bot.Position.Length >= 2 && bot.Position[0] == target[0] && bot.Position[1] == target[1])
        {
            return true;
        }

        if (planningContext.ReservedTargets.Contains((target[0], target[1])))
        {
            return false;
        }

        return IsWalkable(target, state, planningContext.OccupiedPositions);
    }

    private static IEnumerable<int[]> GetAdjacentTargets(int[] itemPosition)
    {
        if (itemPosition.Length < 2)
        {
            return [];
        }

        return
        [
            [itemPosition[0], itemPosition[1] - 1],
            [itemPosition[0] + 1, itemPosition[1]],
            [itemPosition[0], itemPosition[1] + 1],
            [itemPosition[0] - 1, itemPosition[1]]
        ];
    }

    private static Queue<(int X, int Y)> GetCourierRoute(Bot bot, int[] deliveryTarget, GameState state)
    {
        if (CourierRoutes.TryGetValue(bot.Id, out var existingRoute))
        {
            var nextNode = existingRoute.Count > 0 ? existingRoute.Peek() : ((int X, int Y)?)null;
            if (nextNode != null &&
                (bot.Position[0] == nextNode.Value.X && bot.Position[1] == nextNode.Value.Y))
            {
                existingRoute.Dequeue();
            }

            if (existingRoute.Count > 0)
            {
                return existingRoute;
            }
        }

        var route = new Queue<(int X, int Y)>(GetPathNodes(bot.Position, deliveryTarget, state).Skip(1));
        CourierRoutes[bot.Id] = route;
        return route;
    }

    private static (int X, int Y)? GetNextCourierNode(Bot bot, Queue<(int X, int Y)> route)
    {
        while (route.Count > 0)
        {
            var next = route.Peek();
            if (bot.Position[0] == next.X && bot.Position[1] == next.Y)
            {
                route.Dequeue();
                continue;
            }

            return next;
        }

        return null;
    }

    private static void AdvanceCourierRoute(int botId, int[] nextPosition)
    {
        if (nextPosition.Length < 2 || !CourierRoutes.TryGetValue(botId, out var route) || route.Count == 0)
        {
            return;
        }

        var nextNode = route.Peek();
        if (nextNode.X == nextPosition[0] && nextNode.Y == nextPosition[1])
        {
            route.Dequeue();
        }

        if (route.Count == 0)
        {
            CourierRoutes.Remove(botId);
        }
    }

    private static string? FindBestImmediateStep(
        int[] start,
        int[] target,
        GameState state,
        HashSet<(int X, int Y)> occupiedPositions,
        HashSet<(int X, int Y)> reservedCorridorCells,
        bool prioritizeCourier)
    {
        if (start.Length < 2 || target.Length < 2)
        {
            return null;
        }

        var startNode = (X: start[0], Y: start[1]);
        var currentDistance = GetPathDistance(start, target, state);

        return GetNeighbors(startNode)
            .Where(neighbor => IsWalkable([neighbor.X, neighbor.Y], state, occupiedPositions))
            .Where(neighbor => prioritizeCourier || !reservedCorridorCells.Contains(neighbor))
            .Where(neighbor =>
            {
                if (!prioritizeCourier)
                {
                    return true;
                }

                var candidateDistance = GetPathDistance([neighbor.X, neighbor.Y], target, state);
                return candidateDistance <= currentDistance;
            })
            .OrderBy(neighbor => GetPathDistance([neighbor.X, neighbor.Y], target, state))
            .ThenBy(neighbor => Math.Abs(neighbor.X - target[0]) + Math.Abs(neighbor.Y - target[1]))
            .Select(neighbor => ToAction(startNode, neighbor))
            .FirstOrDefault();
    }

    private static int CountAdjacentBots((int X, int Y) point, HashSet<(int X, int Y)> occupiedPositions)
    {
        var count = 0;
        foreach (var neighbor in GetNeighbors(point))
        {
            if (occupiedPositions.Contains(neighbor))
            {
                count++;
            }
        }

        return count;
    }

    private static string ToAction((int X, int Y) start, (int X, int Y) next)
    {
        if (next.X == start.X && next.Y == start.Y - 1)
        {
            return "move_up";
        }

        if (next.X == start.X + 1 && next.Y == start.Y)
        {
            return "move_right";
        }

        if (next.X == start.X && next.Y == start.Y + 1)
        {
            return "move_down";
        }

        if (next.X == start.X - 1 && next.Y == start.Y)
        {
            return "move_left";
        }

        return "wait";
    }
}
