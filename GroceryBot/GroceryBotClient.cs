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
        
        
        Console.WriteLine("Connected. Bot is moving. Press Ctrl+C to stop.");

        /*var receiveTask = Task.Run(() => Receive(cts.Token), cts.Token);
        await Decide(cts.Token);
        await receiveTask; */
    }

    private static async Task MoveToward(string botId, int x, int y, List<int> target)
    {
        
    }
    
    
    
    private static async Task Decide(CancellationToken ct, Bot bot)
    {
        var directions = new[] { "up", "right", "down", "left" };
        var i = 0;
        var x = bot.Position[0];
        var y = bot.Position[1];
        
        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var direction = directions[i % directions.Length];
            i++;

            var payload = JsonSerializer.Serialize(new
            {
                action = "move",
                direction
            });

            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(payload),
                WebSocketMessageType.Text,
                true,
                ct
            );

            Console.WriteLine($"-> {payload}");
            await Task.Delay(450, ct);
        }
    }

    private static async Task Receive( CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];

        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await _socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"Server closed: {result.CloseStatus} {result.CloseStatusDescription}");
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            Console.WriteLine($"<- {text}");
        }
    }
}