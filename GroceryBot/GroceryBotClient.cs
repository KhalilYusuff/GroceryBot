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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
        var nextAction = FindPathAction(bot.Position, target, state, occupiedPositions);
        return CreateResponse(bot.Id, nextAction);
    }

    private static Actions[] BuildActions(GameState state)
    {
        var occupiedPositions = state.Bots
            .Where(bot => bot.Position.Length >= 2)
            .Select(bot => (bot.Position[0], bot.Position[1]))
            .ToHashSet();

        var actions = new List<Actions>(state.Bots.Count);

        foreach (var bot in state.Bots.OrderBy(bot => bot.Id))
        {
            occupiedPositions.Remove((bot.Position[0], bot.Position[1]));

            var response = Decide(bot, state, occupiedPositions);
            var action = response.Actions[0];
            var nextPosition = GetNextPosition(bot.Position, action.Action);

            if (action.Action != "wait" && nextPosition.Length >= 2)
            {
                occupiedPositions.Add((nextPosition[0], nextPosition[1]));
            }

            actions.Add(action);
        }

        return actions.ToArray();
    }
    
    private static BotResponse Decide(Bot bot, GameState state, HashSet<(int X, int Y)> occupiedPositions)
    {
        var x = bot.Position[0];
        var y = bot.Position[1];
        var dropOff = state.Drop_off;
        var active = state.Orders.FirstOrDefault(o => o.Status == "active");

        if (active == null)
        {
            return CreateResponse(bot.Id, "wait");
        }

        var outstandingNeeded = new List<string>(active.Items_required);

        foreach (var delivered in active.Items_delivered)
        {
            outstandingNeeded.Remove(delivered);
        }

        var needed = new List<string>(outstandingNeeded);
        foreach (var carried in bot.Inventory)
        {
            needed.Remove(carried);
        }

        var deliverableRemaining = new List<string>(outstandingNeeded);
        var hasDeliverableItem = false;
        foreach (var carried in bot.Inventory)
        {
            if (!deliverableRemaining.Contains(carried))
            {
                continue;
            }

            deliverableRemaining.Remove(carried);
            hasDeliverableItem = true;
        }

        if (hasDeliverableItem && bot.Position.SequenceEqual(dropOff))
        {
            return CreateResponse(bot.Id, "drop_off");
        }

        if (bot.Inventory.Count >= 3)
        {
            return MoveToward(bot, state, dropOff, occupiedPositions);
        }
        
        foreach (var item in state.Items)
        {
            if (needed.Contains(item.Type))
            {
                var ix = item.Position[0];
                var iy = item.Position[1];

                if (Math.Abs(ix - x) + Math.Abs(iy - y) == 1)
                {
                    Console.WriteLine($"Picking up: {item.Type} id: {item.Id}");
                    return CreateResponse(bot.Id, "pick_up", item.Id);
                }
            }
        }
        
        var candidateItems = state.Items
            .Where(item => needed.Contains(item.Type))
            .Select(item => new
            {
                Item = item,
                Target = FindAdjacentWalkableTarget(item.Position, state, occupiedPositions, bot.Position)
            })
            .Where(candidate => candidate.Target != null)
            .OrderBy(candidate => GetPathDistance(bot.Position, candidate.Target!, state, occupiedPositions))
            .ToList();

        foreach (var candidate in candidateItems)
        {
            var response = MoveToward(bot, state, candidate.Target!, occupiedPositions);
            LogMoveDiagnostics(bot, state, candidate.Target!, response.Actions[0].Action);
            return response;
        }

        if (bot.Inventory.Count > 0)
        {
            Console.WriteLine($"moving towards: {dropOff}");
            var response = MoveToward(bot, state, dropOff, occupiedPositions);
            LogMoveDiagnostics(bot, state, dropOff, response.Actions[0].Action);
            return response;
        }

        return CreateResponse(bot.Id, "wait");
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

            var mappedResult = JsonSerializer.Deserialize<GameState>(payload.ToString(), JsonOptions);
            if (mappedResult != null)
            {
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

    private static void LogMoveDiagnostics(Bot bot, GameState state, int[] target, string action)
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
            $"diag: bot={bot.Id} current={FormatPoint(bot.Position)} target={FormatPoint(target)} " +
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

    private static int[]? FindAdjacentWalkableTarget(
        int[] itemPosition,
        GameState state,
        HashSet<(int X, int Y)> occupiedPositions,
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
            .OrderBy(candidate => GetPathDistance(botPosition, candidate, state, occupiedPositions))
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
        HashSet<(int X, int Y)> occupiedPositions)
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
                if (visited.Contains(neighbor) || !IsWalkableStep(neighbor, targetNode, state, occupiedPositions))
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
            return "wait";
        }

        var step = targetNode;
        while (previous.TryGetValue(step, out var parent) && parent != startNode)
        {
            step = parent;
        }

        return ToAction(startNode, step);
    }

    private static int GetPathDistance(
        int[] start,
        int[] target,
        GameState state,
        HashSet<(int X, int Y)> occupiedPositions)
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
                if (visited.Contains(neighbor) || !IsWalkableStep(neighbor, targetNode, state, occupiedPositions))
                {
                    continue;
                }

                visited.Add(neighbor);
                queue.Enqueue((neighbor, distance + 1));
            }
        }

        return int.MaxValue;
    }

    private static IEnumerable<(int X, int Y)> GetNeighbors((int X, int Y) point)
    {
        yield return (point.X, point.Y - 1);
        yield return (point.X + 1, point.Y);
        yield return (point.X, point.Y + 1);
        yield return (point.X - 1, point.Y);
    }

    private static bool IsWalkableStep(
        (int X, int Y) position,
        (int X, int Y) target,
        GameState state,
        HashSet<(int X, int Y)> occupiedPositions)
    {
        if (position.X == target.X && position.Y == target.Y)
        {
            return position.X >= 0 &&
                   position.X < state.Grid.Width &&
                   position.Y >= 0 &&
                   position.Y < state.Grid.Height &&
                   !state.Grid.Walls.Any(w => w.Length >= 2 && w[0] == position.X && w[1] == position.Y) &&
                   !state.Items.Any(item => item.Position.Length >= 2 && item.Position[0] == position.X && item.Position[1] == position.Y) &&
                   !occupiedPositions.Contains(position);
        }

        return IsWalkable([position.X, position.Y], state, occupiedPositions);
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
