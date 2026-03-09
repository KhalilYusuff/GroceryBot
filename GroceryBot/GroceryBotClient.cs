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

        /*var receiveTask = Task.Run(() => Receive(cts.Token), cts.Token);
        await Decide(cts.Token);
        await receiveTask; */
    }

    private static async Task MoveToward(string botId, int x, int y, List<int> target)
    {
        
    }
    
    private static async Task<BotResponse> Decide(CancellationToken ct, Bot bot, GameState state)
    {
        var xy = bot.Position;
        var dropOff = state.Drop_off;

        if (bot.Inventory.Count > 0 && xy == dropOff)
        {
            var botResponse = new BotResponse();
            var action = new Actions();
            action.BotId = bot.Id;
            action.Action = "dop_off";
            return botResponse;
        }
        
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
                mappedResult = JsonSerializer.Deserialize<GameState>(json);
                
                Console.WriteLine($"Round: {mappedResult?.Round}");

            } while (!result.EndOfMessage);

            return mappedResult;
        }

        return new GameState();
    }
}