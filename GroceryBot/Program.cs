using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var wsUrl = args.FirstOrDefault()
            ?? Environment.GetEnvironmentVariable("GROCERY_WS_URL");

if (string.IsNullOrWhiteSpace(wsUrl))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- <wss-url>");
    Console.WriteLine("or set env var GROCERY_WS_URL");
    return;
}

using var socket = new ClientWebSocket();
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Connecting to: {wsUrl}");

try
{
    await socket.ConnectAsync(new Uri(wsUrl), cts.Token);
}
catch (WebSocketException ex)
{
    Console.WriteLine($"Connection failed: {ex.Message}");
    Console.WriteLine("If you got 403, generate a fresh WebSocket URL/token and try again.");
    return;
}

Console.WriteLine("Connected. Bot is moving. Press Ctrl+C to stop.");

var receiveTask = Task.Run(() => ReceiveLoop(socket, cts.Token), cts.Token);
await MoveLoop(socket, cts.Token);
await receiveTask;

static async Task MoveLoop(ClientWebSocket socket, CancellationToken ct)
{
    var directions = new[] { "up", "right", "down", "left" };
    var i = 0;

    while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        var direction = directions[i % directions.Length];
        i++;

        var payload = JsonSerializer.Serialize(new
        {
            action = "move",
            direction
        });

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(payload),
            WebSocketMessageType.Text,
            true,
            ct
        );

        Console.WriteLine($"-> {payload}");
        await Task.Delay(450, ct);
    }
}

static async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
{
    var buffer = new byte[8 * 1024];

    while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, ct);

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
