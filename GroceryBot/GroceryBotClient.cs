using GroceryBot.Model;

namespace GroceryBot;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DotNetEnv;

public class GroceryBotClient
{
    
    //this logic is not sufficient. Will use the docs https://app.ainm.no/docs/game

    private static string? _wsUrl; 
    private static ClientWebSocket _socket = new ClientWebSocket();
    public GroceryBotClient()
    {
    }

    public static async Task Play()
    {
        Env.Load();
        _wsUrl = Environment.GetEnvironmentVariable("GROCERY_WS_URL");
        
        if (String.IsNullOrEmpty(_wsUrl))
        {
            Console.WriteLine($"url must not be empty.");
            return;
        }
        using var socket = new ClientWebSocket();
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

        var state = Receive(cts.Token);
        
        Console.WriteLine("Connected. Bot is moving. Press Ctrl+C to stop.");
        
        while (true)
        {
            var receiveTask = Task.Run(() => Receive(cts.Token), cts.Token);
            Console.WriteLine($"recieved: {receiveTask.Result.ToString()}");
            Decide(receiveTask.Result.Bots[0], receiveTask.Result);
            
        }

    }

    private static BotResponse MoveToward(int botId, int x, int y, int[] target)
    {
        var targetX = target[0];
        var targetY = target[1];
        var botResponse = new BotResponse();
        var action = new Actions();

        if (Math.Abs(targetX - x) > Math.Abs(targetY - y))
        {
            if (targetX > x)
            {
                action.Action = "move_right";
                action.BotId = botId;
                botResponse.Actions[0] = action;
                
            }
            else
            {
                action.Action = "move_left";
                botResponse.Actions[0] = action;
                
            }
            return botResponse;
        }
        if (targetY != y)
        {
            
            if (targetY > y)
            {
                action.Action = "move_down";
                action.BotId = botId;

                botResponse.Actions[0] = action;
            }

            else
            {
                action.Action = "move_up";
                action.BotId = botId;
                botResponse.Actions[0] = action;
            }

            return botResponse;
        }
        action.Action = "wait";
        action.BotId = botId;
        botResponse.Actions[0] = action;
        return botResponse;
    }
    
    private static BotResponse Decide(Bot bot, GameState state)
    {
        var x = bot.Position[0];
        var y = bot.Position[1];
        var dropOff = state.Drop_off;
        var botResponse = new BotResponse();
        var action = new Actions();

        //if at a dropOff cord and inventory is not empty, drop off the items in inventory
        if (bot.Inventory.Count > 0 && bot.Position == dropOff)
        {
            action.Action = "dop_off";
            botResponse.Actions[0].BotId = bot.Id;
            botResponse.Actions[0] = action;
            return botResponse;
        }
        
        if (bot.Inventory.Count >= 3)
        {
            return MoveToward(bot.Id, x, y, dropOff);
        }
        
        var active = state.Orders.FirstOrDefault(o => o.Status == "active");
        if (active == null)
        {
            action.Action = "wait";
            botResponse.Actions[0] = action;
            return botResponse;
        }

        var needed = new List<string>(active.Items_delivered);

        foreach (var delivered in active.Items_delivered)
        {
            if (needed.Contains(delivered))
            {
                needed.Remove(delivered);
            }
        }

        foreach (var item in state.Items)
        {
            if (needed.Contains(item.Type))
            {
                var ix = item.Position[0];
                var iy = item.Position[1];

                if (Math.Abs(ix - x) + Math.Abs(iy - y) == 1)
                {
                    action.BotId = bot.Id;
                    action.Action = "pick_up";
                    action.ItemId = item.Id;
                    botResponse.Actions[0] = action;

                    return botResponse;
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

        action.BotId = bot.Id;
        action.Action = "wait";
        botResponse.Actions[0] = action;
        return botResponse;
    }

    private static async Task<GameState> Receive( CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];

        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            GameState? mappedResult;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"Server closed: {result.CloseStatus} {result.CloseStatusDescription}");
                    return new GameState();
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                
                Console.WriteLine($"received JSON: {json.ToString()} ");
                
                mappedResult = JsonSerializer.Deserialize<GameState>(json);
                
                Console.WriteLine($"Round: {mappedResult?.Round}");

            } while (!result.EndOfMessage);

            return mappedResult;
        }

        return new GameState();
    }
}