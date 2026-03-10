using GroceryBot.Model;
using System.Linq;
using System.Collections.Generic;

namespace GroceryBot;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DotNetEnv;

public class GroceryBotClient
{
    private static string? _wsUrl; 
    private static readonly ClientWebSocket _socket = new();
    private static readonly Dictionary<int, (int X, int Y)> LastPositions = new();
    private static readonly Dictionary<int, int> StuckRounds = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class PlanningContext
    {
        public HashSet<(int X, int Y)> OccupiedPositions { get; init; } = [];
        public HashSet<(int X, int Y)> ReservedTargets { get; init; } = [];
        public HashSet<(int X, int Y)> ReservedCorridorCells { get; init; } = [];
        public Dictionary<string, List<string>> CommittedTypesByOrderId { get; init; } = [];
        public HashSet<string> ReservedItemIds { get; init; } = [];
    }

    private sealed class BotTask
    {
        public required string Kind { get; init; }
        public string? OrderId { get; init; }
        public string? ItemId { get; init; }
        public string? ItemType { get; init; }
        public int[]? Target { get; init; }
    }

    private const int EndgameRounds = 35;

    public static async Task Play()
    {
        Env.Load();
        
        _wsUrl = Environment.GetEnvironmentVariable("GROCERY_WS_URL");
        
        if (String.IsNullOrEmpty(_wsUrl))
        {
            Console.WriteLine($"url must not be empty.");
            return;
        }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        
        Console.WriteLine($"Connecting to websocket");
        
        try
        {
            await _socket.ConnectAsync(new Uri(_wsUrl), cts.Token);
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"Connection failed: {ex.Message}");
            Console.WriteLine("In case of 403 generate fresh token");
            return;
        }
        
        Console.WriteLine("Connected. Bot is moving. Press Ctrl+C to stop.");
        
        while (true)
        {
            var state = await Receive(cts.Token);
            
            Console.WriteLine($"received: {state}");

            if (state.Type == "game_over")
            {
                Console.WriteLine($"game over! Score: {state.Score}");
                break;
            }

            if (state.Bots.Count == 0)
            {
                Console.WriteLine("Bots is empty or null");
                break;
            }
            
            var decision = new BotResponse
            {
                Actions = BuildActions(state)
            };
            Console.WriteLine($"decision: {decision}");
            Console.WriteLine();
            var payload = JsonSerializer.Serialize(decision, JsonOptions);
            //Console.WriteLine($"payload: {payload}");

            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(payload),
                WebSocketMessageType.Text,
                true,
                cts.Token
            );
        }
    }

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

    private static Actions[] BuildActions(GameState state)
    {
        UpdateProgressTracking(state);

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
            ReservedItemIds = []
        };

        var actions = new List<Actions>(state.Bots.Count);
        var tasksByBotId = AllocateTasks(state, planningOrder, deliveryTargets);
        ReserveDeliveryCorridors(state, planningOrder, tasksByBotId, planningContext);

        foreach (var bot in planningOrder)
        {
            planningContext.OccupiedPositions.Remove((bot.Position[0], bot.Position[1]));

            var task = tasksByBotId.GetValueOrDefault(bot.Id) ?? new BotTask { Kind = "wait" };
            Console.WriteLine(
                $"task: bot={bot.Id} role={task.Kind} target={FormatPoint(task.Target ?? [])} item={task.ItemId ?? "-"}");
            var response = ExecuteTask(bot, task, state, planningContext, deliveryTargets);
            var action = response.Actions[0];
            var nextPosition = GetNextPosition(bot.Position, action.Action);

            if (action.Action != "wait" && nextPosition.Length >= 2)
            {
                planningContext.OccupiedPositions.Add((nextPosition[0], nextPosition[1]));
            }

            CommitBotPlan(bot, state, action, planningContext);
            actions.Add(action);
        }

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

        if (active == null)
        {
            foreach (var bot in planningOrder)
            {
                tasksByBotId[bot.Id] = bot.Inventory.Count > 0
                    ? new BotTask { Kind = "deliver", Target = GetBestTarget(bot.Position, deliveryTargets, state) }
                    : new BotTask { Kind = "stage", Target = GetBestStageTarget(bot, state, deliveryTargets, tasksByBotId.Values.ToList(), endgame) };
            }

            return tasksByBotId;
        }

        var activeDemand = GetOutstandingCounts(active);
        var previewDemand = preview == null ? new Dictionary<string, int>() : GetOutstandingCounts(preview);
        var availableBots = new List<Bot>(planningOrder);

        foreach (var bot in planningOrder)
        {
            if (bot.Inventory.Count == 0)
            {
                continue;
            }

            if (CountMatchingInventory(bot.Inventory, activeDemand) > 0 || bot.Inventory.Count >= 3)
            {
                tasksByBotId[bot.Id] = new BotTask
                {
                    Kind = "deliver",
                    Target = GetBestTarget(bot.Position, deliveryTargets, state)
                };
                ConsumeMatchingInventory(bot.Inventory, activeDemand);
                availableBots.Remove(bot);
            }
        }

        var activeAssignments = AssignPickupTasks(availableBots, state, active, activeDemand, "pickup_active");
        foreach (var pair in activeAssignments)
        {
            tasksByBotId[pair.Key] = pair.Value;
            availableBots.RemoveAll(bot => bot.Id == pair.Key);
        }

        var activeCovered = activeDemand.Values.All(count => count <= 0);
        if (!endgame && preview != null && activeCovered)
        {
            foreach (var bot in availableBots.ToList())
            {
                if (bot.Inventory.Count > 0 && CountMatchingInventory(bot.Inventory, previewDemand) > 0)
                {
                    tasksByBotId[bot.Id] = new BotTask
                    {
                        Kind = "pickup_preview",
                        OrderId = preview.Id
                    };
                    ConsumeMatchingInventory(bot.Inventory, previewDemand);
                    availableBots.Remove(bot);
                }
            }

            var previewAssignments = AssignPickupTasks(availableBots, state, preview, previewDemand, "pickup_preview");
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
                Kind = "stage",
                Target = GetBestStageTarget(bot, state, deliveryTargets, assignedTasks, endgame)
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
        string taskKind)
    {
        var tasks = new Dictionary<int, BotTask>();
        if (candidateBots.Count == 0)
        {
            return tasks;
        }

        var candidateItems = state.Items
            .Where(item => demand.GetValueOrDefault(item.Type) > 0)
            .ToList();

        var usedBotIds = new HashSet<int>();
        var usedItemIds = new HashSet<string>();

        while (true)
        {
            Bot? bestBot = null;
            Item? bestItem = null;
            var bestDistance = int.MaxValue;

            foreach (var bot in candidateBots)
            {
                if (usedBotIds.Contains(bot.Id) || bot.Inventory.Count > 0)
                {
                    continue;
                }

                foreach (var item in candidateItems)
                {
                    if (usedItemIds.Contains(item.Id) || demand.GetValueOrDefault(item.Type) <= 0)
                    {
                        continue;
                    }

                    var target = FindAdjacentWalkableTarget(item.Position, state, [], [], bot.Position);
                    if (target == null)
                    {
                        continue;
                    }

                    var distance = GetPathDistance(bot.Position, target, state);
                    if (distance >= bestDistance)
                    {
                        continue;
                    }

                    bestDistance = distance;
                    bestBot = bot;
                    bestItem = item;
                }
            }

            if (bestBot == null || bestItem == null)
            {
                break;
            }

            tasks[bestBot.Id] = new BotTask
            {
                Kind = taskKind,
                OrderId = order.Id,
                ItemId = bestItem.Id,
                ItemType = bestItem.Type
            };
            usedBotIds.Add(bestBot.Id);
            usedItemIds.Add(bestItem.Id);
            demand[bestItem.Type] = demand.GetValueOrDefault(bestItem.Type) - 1;
        }

        return tasks;
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
            return CreateResponse(bot.Id, "drop_off");
        }

        return ExecuteMoveTask(bot, state, planningContext, deliveryTarget, true, role);
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
                return CreateResponse(bot.Id, "pick_up", item.Id);
            }
        }

        var target = FindAdjacentWalkableTarget(
            item.Position,
            state,
            planningContext.OccupiedPositions,
            planningContext.ReservedTargets,
            bot.Position);

        if (target == null)
        {
            return CreateResponse(bot.Id, FindRecoveryAction(bot, state, planningContext.OccupiedPositions, []));
        }

        planningContext.ReservedItemIds.Add(item.Id);
        planningContext.ReservedTargets.Add((target[0], target[1]));
        if (!string.IsNullOrEmpty(task.OrderId) && !string.IsNullOrEmpty(item.Type))
        {
            AddCommittedType(planningContext, task.OrderId, item.Type);
        }

        return ExecuteMoveTask(bot, state, planningContext, target, false, task.Kind);
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
    
    private static BotResponse MoveTowardNearest(
        Bot bot,
        GameState state,
        List<int[]> targets,
        HashSet<(int X, int Y)> occupiedPositions)
    {
        var target = GetBestTarget(bot.Position, targets, state);
        return MoveToward(bot, state, target, occupiedPositions);
    }

    private static async Task<GameState> Receive( CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];

        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            var payload = new StringBuilder();

            do
            {
                result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"Server closed: {result.CloseStatus} {result.CloseStatusDescription}");
                    return new GameState();
                }

                payload.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            } while (!result.EndOfMessage);

            var payloadText = payload.ToString();
            var mappedResult = JsonSerializer.Deserialize<GameState>(payloadText, JsonOptions);
            if (mappedResult != null)
            {
                PopulateFallbackFields(mappedResult, payloadText);
                Console.WriteLine($"Round: {mappedResult.Round}");
                return mappedResult;
            }
        }

        return new GameState();
    }

    private static BotResponse CreateResponse(int botId, string action, string? itemId = null)
    {
        return new BotResponse
        {
            Actions =
            [
                new Actions
                {
                    BotId = botId,
                    Action = action,
                    ItemId = itemId
                }
            ]
        };
    }

    private static void LogMoveDiagnostics(Bot bot, GameState state, int[] target, string action, string role)
    {
        var next = GetNextPosition(bot.Position, action);
        var isWall = state.Grid.Walls.Any(w => w.Length >= 2 && w[0] == next[0] && w[1] == next[1]);
        var isShelf = state.Items.Any(item => item.Position.Length >= 2 && item.Position[0] == next[0] && item.Position[1] == next[1]);
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

    private static int[] GetBestTargetForLogging(int[] start, List<int[]> targets)
    {
        if (targets.Count == 0)
        {
            return [];
        }

        return targets
            .OrderBy(target => Math.Abs(start[0] - target[0]) + Math.Abs(start[1] - target[1]))
            .First();
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
            ? new[] { Math.Max(1, state.Grid.Height - 5), Math.Max(1, state.Grid.Height - 6), Math.Max(1, state.Grid.Height - 4) }
            : new[] { Math.Max(1, state.Grid.Height - 4), Math.Max(1, state.Grid.Height - 5), Math.Max(1, state.Grid.Height - 3) };
        var minStageX = Math.Max(1, state.Grid.Width / 2);
        foreach (var y in preferredRows)
        {
            for (var x = minStageX; x < state.Grid.Width - 1; x++)
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
            .OrderBy(candidate => GetPathDistance(bot.Position, candidate, state))
            .ThenByDescending(candidate => deliveryTargets.Count == 0
                ? 0
                : deliveryTargets.Min(target => Math.Abs(candidate[0] - target[0]) + Math.Abs(candidate[1] - target[1])))
            .ThenByDescending(candidate => candidate[0])
            .First();
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

        if (position[1] >= state.Grid.Height - 2)
        {
            return false;
        }

        return !state.Grid.Walls.Any(w => w.Length >= 2 && w[0] == position[0] && w[1] == position[1]) &&
               !state.Items.Any(item => item.Position.Length >= 2 && item.Position[0] == position[0] && item.Position[1] == position[1]);
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
            .OrderBy(candidate => GetPathDistance(botPosition, candidate, state))
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

        if (state.Grid.Walls.Any(w => w.Length >= 2 && w[0] == x && w[1] == y))
        {
            return false;
        }

        if (state.Items.Any(item => item.Position.Length >= 2 && item.Position[0] == x && item.Position[1] == y))
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
                if (visited.Contains(neighbor) || !IsPathNodeWalkable(neighbor, targetNode, state))
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

        var startNode = (X: start[0], Y: start[1]);
        var targetNode = (X: target[0], Y: target[1]);
        var queue = new Queue<((int X, int Y) Node, int Distance)>();
        var visited = new HashSet<(int X, int Y)> { startNode };

        queue.Enqueue((startNode, 0));

        while (queue.Count > 0)
        {
            var (current, distance) = queue.Dequeue();
            if (current == targetNode)
            {
                return distance;
            }

            foreach (var neighbor in GetNeighbors(current))
            {
                if (visited.Contains(neighbor) || !IsPathNodeWalkable(neighbor, targetNode, state))
                {
                    continue;
                }

                visited.Add(neighbor);
                queue.Enqueue((neighbor, distance + 1));
            }
        }

        return int.MaxValue;
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
                if (visited.Contains(neighbor) || !IsPathNodeWalkable(neighbor, targetNode, state))
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

    private static IEnumerable<(int X, int Y)> GetNeighbors((int X, int Y) point)
    {
        yield return (point.X, point.Y - 1);
        yield return (point.X + 1, point.Y);
        yield return (point.X, point.Y + 1);
        yield return (point.X - 1, point.Y);
    }

    private static bool IsPathNodeWalkable(
        (int X, int Y) position,
        (int X, int Y) target,
        GameState state)
    {
        if (position.X == target.X && position.Y == target.Y)
        {
            return position.X >= 0 &&
                   position.X < state.Grid.Width &&
                   position.Y >= 0 &&
                   position.Y < state.Grid.Height &&
                   !state.Grid.Walls.Any(w => w.Length >= 2 && w[0] == position.X && w[1] == position.Y) &&
                   !state.Items.Any(item => item.Position.Length >= 2 && item.Position[0] == position.X && item.Position[1] == position.Y);
        }

        return position.X >= 0 &&
               position.X < state.Grid.Width &&
               position.Y >= 0 &&
               position.Y < state.Grid.Height &&
               !state.Grid.Walls.Any(w => w.Length >= 2 && w[0] == position.X && w[1] == position.Y) &&
               !state.Items.Any(item => item.Position.Length >= 2 && item.Position[0] == position.X && item.Position[1] == position.Y);
    }

    private static void UpdateProgressTracking(GameState state)
    {
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

    private static void CommitBotPlan(Bot bot, GameState state, Actions action, PlanningContext planningContext)
    {
        foreach (var order in state.Orders.Where(order => order.Status == "active" || order.Status == "preview").Take(2))
        {
            var remaining = GetOutstandingNeeded(order, planningContext);
            foreach (var carried in bot.Inventory)
            {
                if (!remaining.Remove(carried))
                {
                    continue;
                }

                AddCommittedType(planningContext, order.Id, carried);
            }
        }

        if (action.Action == "pick_up" && !string.IsNullOrEmpty(action.ItemId))
        {
            if (!planningContext.ReservedItemIds.Add(action.ItemId))
            {
                return;
            }

            var pickedItem = state.Items.FirstOrDefault(item => item.Id == action.ItemId);
            if (pickedItem != null)
            {
                foreach (var order in state.Orders.Where(order => order.Status == "active" || order.Status == "preview").Take(2))
                {
                    var remaining = GetOutstandingNeeded(order, planningContext);
                    if (!remaining.Remove(pickedItem.Type))
                    {
                        continue;
                    }

                    AddCommittedType(planningContext, order.Id, pickedItem.Type);
                    break;
                }
            }

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
    }

    private static void ReserveDeliveryCorridors(
        GameState state,
        List<Bot> planningOrder,
        Dictionary<int, BotTask> tasksByBotId,
        PlanningContext planningContext)
    {
        foreach (var bot in planningOrder)
        {
            if (!tasksByBotId.TryGetValue(bot.Id, out var task) || task.Kind != "deliver")
            {
                continue;
            }

            var target = task.Target ?? [];
            foreach (var node in GetPathNodes(bot.Position, target, state).Take(6))
            {
                planningContext.ReservedCorridorCells.Add(node);
            }
        }
    }

    private static string FindRecoveryAction(
        Bot bot,
        GameState state,
        HashSet<(int X, int Y)> occupiedPositions,
        int[] preferredTarget)
    {
        var isStuck = StuckRounds.GetValueOrDefault(bot.Id) >= 2;
        if (!isStuck)
        {
            return "wait";
        }

        return FindBestImmediateStep(bot.Position, preferredTarget, state, occupiedPositions, [], false) ?? "wait";
    }

    private static string? FindBestImmediateStep(
        int[] start,
        int[] target,
        GameState state,
        HashSet<(int X, int Y)> occupiedPositions,
        HashSet<(int X, int Y)> reservedCorridorCells,
        bool prioritizeCourier)
    {
        if (start.Length < 2)
        {
            return null;
        }

        if (target.Length < 2)
        {
            return null;
        }

        var startNode = (X: start[0], Y: start[1]);

        return GetNeighbors(startNode)
            .Where(neighbor => IsWalkable([neighbor.X, neighbor.Y], state, occupiedPositions))
            .Where(neighbor => prioritizeCourier || !reservedCorridorCells.Contains(neighbor))
            .OrderBy(neighbor => GetPathDistance([neighbor.X, neighbor.Y], target, state))
            .ThenBy(neighbor => Math.Abs(neighbor.X - target[0]) + Math.Abs(neighbor.Y - target[1]))
            .Select(neighbor => ToAction(startNode, neighbor))
            .FirstOrDefault();
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

    private static List<string> GetOutstandingNeeded(Order order, PlanningContext planningContext)
    {
        var outstandingNeeded = GetOutstandingNeeded(order);

        if (planningContext.CommittedTypesByOrderId.TryGetValue(order.Id, out var committed))
        {
            foreach (var committedType in committed)
            {
                outstandingNeeded.Remove(committedType);
            }
        }

        return outstandingNeeded;
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

    private static void AddCommittedType(PlanningContext planningContext, string orderId, string itemType)
    {
        if (!planningContext.CommittedTypesByOrderId.TryGetValue(orderId, out var committed))
        {
            committed = [];
            planningContext.CommittedTypesByOrderId[orderId] = committed;
        }

        committed.Add(itemType);
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
