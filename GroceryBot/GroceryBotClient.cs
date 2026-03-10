using GroceryBot.Model;
using System.Linq;
using System.Collections.Generic;

namespace GroceryBot;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotNetEnv;

public class GroceryBotClient
{
    private static string? _wsUrl; 
    private static readonly ClientWebSocket _socket = new();
    private static readonly Dictionary<int, (int X, int Y)> LastPositions = new();
    private static readonly Dictionary<int, int> StuckRounds = new();
    private static readonly Dictionary<int, (string Action, int X, int Y)> LastPlannedMoves = new();
    private static readonly Dictionary<int, (string Action, string? ItemId)> LastActionAttempts = new();
    private static readonly Dictionary<int, Queue<(int X, int Y)>> CourierRoutes = new();
    private static readonly Dictionary<int, (string ItemId, int X, int Y)> PickupAccessTargets = new();
    private static GameState? LastObservedState;
    private static string? LastPayload;
    private static RoundCache? CurrentRoundCache;
    private static bool DebugLogging;
    private static bool TimingLogging;
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

    private sealed class RoundCache
    {
        public required HashSet<(int X, int Y)> WallCells { get; init; }
        public required HashSet<(int X, int Y)> ShelfCells { get; init; }
        public Dictionary<(int StartX, int StartY, int TargetX, int TargetY), int> DistanceCache { get; } = [];
        public Dictionary<(int StartX, int StartY, int TargetX, int TargetY), List<(int X, int Y)>> PathCache { get; } = [];
    }

    private const int EndgameRounds = 35;

    public static async Task Play()
    {
        Env.Load();
        DebugLogging = string.Equals(Environment.GetEnvironmentVariable("GROCERY_DEBUG"), "1", StringComparison.Ordinal);
        TimingLogging = string.Equals(Environment.GetEnvironmentVariable("GROCERY_TIMING"), "1", StringComparison.Ordinal);
        
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

        try
        {
            while (true)
            {
                var state = await Receive(cts.Token);
                
                LogDebug($"received: {state}");
                if (DebugLogging)
                {
                    LogStateDelta(LastObservedState, state, LastPayload);
                    LastObservedState = state;
                }

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

                var roundTimer = Stopwatch.StartNew();
                var decision = new BotResponse
                {
                    Actions = BuildActions(state)
                };
                LogDebug($"decision: {decision}");
                LogDebug(string.Empty);
                var payload = JsonSerializer.Serialize(decision, JsonOptions);
                LogDebug($"payload: {payload}");
                if (DebugLogging)
                {
                    LastPayload = payload;
                }

                await _socket.SendAsync(
                    Encoding.UTF8.GetBytes(payload),
                    WebSocketMessageType.Text,
                    true,
                    cts.Token
                );

                roundTimer.Stop();
                if (TimingLogging && roundTimer.ElapsedMilliseconds >= 1500)
                {
                    Console.WriteLine(
                        $"warning: slow round round={state.Round} decision_ms={roundTimer.ElapsedMilliseconds} actions={decision.Actions.Length}");
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.WriteLine("Cancelled.");
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"WebSocket disconnected: {ex.Message}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Transport closed: {ex.Message}");
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
            ReservedItemIds = []
        };

        var actions = new List<Actions>(state.Bots.Count);
        var tasksByBotId = AllocateTasks(state, planningOrder, deliveryTargets);
        ReserveDeliveryCorridors(state, planningOrder, tasksByBotId, planningContext);

        foreach (var bot in planningOrder)
        {
            planningContext.OccupiedPositions.Remove((bot.Position[0], bot.Position[1]));

            var task = tasksByBotId.GetValueOrDefault(bot.Id) ?? new BotTask { Kind = "wait" };
            LogDebug(
                $"task: bot={bot.Id} role={task.Kind} target={FormatPoint(task.Target ?? [])} item={task.ItemId ?? "-"}");
            var response = ExecuteTask(bot, task, state, planningContext, deliveryTargets);
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
        var smallTeam = planningOrder.Count <= 3;

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
        if (!smallTeam && !endgame && preview != null && activeCovered)
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

        if (smallTeam && activeCovered)
        {
            foreach (var bot in availableBots.ToList())
            {
                if (bot.Inventory.Count > 0)
                {
                    tasksByBotId[bot.Id] = new BotTask
                    {
                        Kind = "deliver",
                        Target = GetBestTarget(bot.Position, deliveryTargets, state)
                    };
                    availableBots.Remove(bot);
                }
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

                    var distance = GetHeuristicDistance(bot.Position, target);
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
        if (task.Kind != "deliver" &&
            bot.Position.Length >= 2 &&
            (planningContext.ReservedCorridorCells.Contains((bot.Position[0], bot.Position[1])) ||
             IsAtAnyTarget(bot.Position, deliveryTargets)))
        {
            var corridorEscape = FindCorridorEscapeAction(bot, state, planningContext);
            if (corridorEscape is { } escape)
            {
                LogMoveDiagnostics(bot, state, escape.Target, escape.Action, $"{task.Kind}:escape");
                return CreateResponse(bot.Id, escape.Action);
            }
        }

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
            CourierRoutes.Remove(bot.Id);
            return CreateResponse(bot.Id, "drop_off");
        }

        var response = ExecuteDeliverMoveTask(bot, state, planningContext, deliveryTarget, role);
        var action = response.Actions[0].Action;
        var nextPosition = GetNextPosition(bot.Position, action);
        AdvanceCourierRoute(bot.Id, nextPosition);
        return response;
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

        var target = GetPickupAccessTarget(bot, item, state, planningContext);

        if (target == null)
        {
            PickupAccessTargets.Remove(bot.Id);
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
        if (state.Drop_off.Length >= 2)
        {
            return [state.Drop_off];
        }

        if (state.Drop_off_zones.Count > 0)
        {
            return state.Drop_off_zones.Where(zone => zone.Length >= 2).ToList();
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
            .OrderBy(candidate => GetHeuristicDistance(bot.Position, candidate))
            .ThenByDescending(candidate => deliveryTargets.Count == 0
                ? 0
                : deliveryTargets.Min(target => GetHeuristicDistance(candidate, target)))
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

    private static bool IsPathNodeWalkable(
        (int X, int Y) position,
        (int X, int Y) target,
        GameState state)
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

    private static RoundCache GetRoundCache(GameState state)
    {
        return CurrentRoundCache ??= CreateRoundCache(state);
    }

    private static RoundCache CreateRoundCache(GameState state)
    {
        var wallCells = state.Grid.Walls
            .Where(wall => wall.Length >= 2)
            .Select(wall => (wall[0], wall[1]))
            .ToHashSet();
        var shelfCells = state.Items
            .Where(item => item.Position.Length >= 2)
            .Select(item => (item.Position[0], item.Position[1]))
            .ToHashSet();

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

    private static void UpdateProgressTracking(GameState state)
    {
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
            Console.WriteLine(
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
            Console.WriteLine($"prev_payload: {previousPayload}");
        }

        if (current.Score != previous.Score)
        {
            Console.WriteLine($"delta: score {previous.Score}->{current.Score}");
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
                Console.WriteLine($"delta: bot={bot.Id} inventory [{previousInventory}] -> [{currentInventory}]");
            }

            if (previousBot.Position.Length >= 2 && bot.Position.Length >= 2 &&
                (previousBot.Position[0] != bot.Position[0] || previousBot.Position[1] != bot.Position[1]))
            {
                Console.WriteLine(
                    $"delta: bot={bot.Id} position [{previousBot.Position[0]}, {previousBot.Position[1]}] -> [{bot.Position[0]}, {bot.Position[1]}]");
            }
        }

        var previousItemIds = previous.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var currentItemIds = current.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removedItemId in previousItemIds.Except(currentItemIds).OrderBy(id => id))
        {
            Console.WriteLine($"delta: item_removed {removedItemId}");
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
                Console.WriteLine($"delta: order={order.Id} delivered [{previousDelivered}] -> [{currentDelivered}]");
            }

            if (!string.Equals(previousOrder.Status, order.Status, StringComparison.Ordinal) ||
                previousOrder.Complete != order.Complete)
            {
                Console.WriteLine(
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
                        LogDebug($"feedback: bot={bot.Id} drop_off_no_effect");
                    }

                    break;
                }
                case "pick_up":
                {
                    var itemRemoved = !string.IsNullOrEmpty(attempt.ItemId) && !currentItems.Contains(attempt.ItemId);
                    if (!inventoryChanged && !itemRemoved)
                    {
                        LogDebug($"feedback: bot={bot.Id} pick_up_no_effect item={attempt.ItemId}");
                    }

                    break;
                }
                default:
                    break;
            }
        }
    }

    private static void LogDebug(string message)
    {
        if (!DebugLogging)
        {
            return;
        }

        Console.WriteLine(message);
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
            PickupAccessTargets.Remove(bot.Id);
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

        if (action.Action == "drop_off")
        {
            CourierRoutes.Remove(bot.Id);
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

    private static (string Action, int[] Target)? FindCorridorEscapeAction(
        Bot bot,
        GameState state,
        PlanningContext planningContext)
    {
        if (bot.Position.Length < 2)
        {
            return null;
        }

        var current = (X: bot.Position[0], Y: bot.Position[1]);
        var deliveryTargets = GetDeliveryTargets(state);
        var onDeliveryTarget = IsAtAnyTarget(bot.Position, deliveryTargets);
        var currentDeliveryDistance = deliveryTargets.Count == 0
            ? int.MaxValue
            : deliveryTargets.Min(target => Math.Abs(current.X - target[0]) + Math.Abs(current.Y - target[1]));

        var safeCandidate = GetNeighbors(current)
            .Where(neighbor => IsWalkable([neighbor.X, neighbor.Y], state, planningContext.OccupiedPositions))
            .Where(neighbor => !planningContext.ReservedCorridorCells.Contains(neighbor))
            .OrderByDescending(neighbor => deliveryTargets.Count == 0
                ? 0
                : deliveryTargets.Min(target => Math.Abs(neighbor.X - target[0]) + Math.Abs(neighbor.Y - target[1])))
            .ThenBy(neighbor => Math.Abs(neighbor.X - current.X) + Math.Abs(neighbor.Y - current.Y))
            .FirstOrDefault();

        if (safeCandidate != default)
        {
            var safeDistance = deliveryTargets.Count == 0
                ? int.MaxValue
                : deliveryTargets.Min(target => Math.Abs(safeCandidate.X - target[0]) + Math.Abs(safeCandidate.Y - target[1]));

            if (safeDistance >= currentDeliveryDistance || onDeliveryTarget)
            {
                return (ToAction(current, safeCandidate), [safeCandidate.X, safeCandidate.Y]);
            }
        }

        if (!onDeliveryTarget)
        {
            return null;
        }

        var emergencyCandidate = GetNeighbors(current)
            .Where(neighbor => IsWalkable([neighbor.X, neighbor.Y], state, planningContext.OccupiedPositions))
            .OrderByDescending(neighbor => deliveryTargets.Count == 0
                ? 0
                : deliveryTargets.Min(target => Math.Abs(neighbor.X - target[0]) + Math.Abs(neighbor.Y - target[1])))
            .ThenBy(neighbor => planningContext.ReservedCorridorCells.Contains(neighbor) ? 1 : 0)
            .FirstOrDefault();

        if (emergencyCandidate == default)
        {
            return null;
        }

        return (ToAction(current, emergencyCandidate), [emergencyCandidate.X, emergencyCandidate.Y]);
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
        if (start.Length < 2)
        {
            return null;
        }

        if (target.Length < 2)
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
