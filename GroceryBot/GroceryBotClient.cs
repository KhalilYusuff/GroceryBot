using GroceryBot.Model;
using System.Linq;

namespace GroceryBot;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DotNetEnv;

public class GroceryBotClient
{
    private static string? _wsUrl; 
    private static readonly ClientWebSocket _socket = new();

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
        
        while (_socket.State == WebSocketState.Open)
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

            var decision = Decide(state.Bots[0], state);
            var payload = JsonSerializer.Serialize(decision);

            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(payload),
                WebSocketMessageType.Text,
                true,
                cts.Token
            );
        }

    }

    private static BotResponse MoveToward(int botId, int x, int y, int[] target)
    {
        var targetX = target[0];
        var targetY = target[1];

        if (Math.Abs(targetX - x) > Math.Abs(targetY - y))
        {
            if (targetX > x)
            {
                return CreateResponse(botId, "move_right");
            }

            return CreateResponse(botId, "move_left");
        }

        if (targetY != y)
        {
            if (targetY > y)
            {
                return CreateResponse(botId, "move_down");
            }

            return CreateResponse(botId, "move_up");
        }

        return CreateResponse(botId, "wait");
    }
    
    private static BotResponse Decide(Bot bot, GameState state)
    {
        var x = bot.Position[0];
        var y = bot.Position[1];
        var dropOff = state.Drop_off;

        if (bot.Inventory.Count > 0 && bot.Position.SequenceEqual(dropOff))
        {
            return CreateResponse(bot.Id, "drop_off");
        }
        
        if (bot.Inventory.Count >= 3)
        {
            return MoveToward(bot.Id, x, y, dropOff);
        }
        
        var active = state.Orders.FirstOrDefault(o => o.Status == "active");
        if (active == null)
        {
            return CreateResponse(bot.Id, "wait");
        }

        var needed = new List<string>(active.Items_required);

        foreach (var delivered in active.Items_delivered)
        {
            needed.Remove(delivered);
        }

        foreach (var item in state.Items)
        {
            if (needed.Contains(item.Type))
            {
                var ix = item.Position[0];
                var iy = item.Position[1];

                if (Math.Abs(ix - x) + Math.Abs(iy - y) == 1)
                {
                    return CreateResponse(bot.Id, "pick_up", item.Id);
                }
            }
        }
        
        foreach (var item in state.Items)
        {
            if (needed.Contains(item.Type))
            {
                return MoveToward(bot.Id, x, y, item.Position);
            }
        }

        if (bot.Inventory.Count > 0)
        {
            return MoveToward(bot.Id, x, y, dropOff);
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

            var mappedResult = JsonSerializer.Deserialize<GameState>(payload.ToString());
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
}
