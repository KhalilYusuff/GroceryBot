using GroceryBot.Model;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DotNetEnv;

namespace GroceryBot;

public partial class GroceryBotClient
{
    public static async Task Play()
    {
        Env.Load();
        DebugLogging = string.Equals(Environment.GetEnvironmentVariable("GROCERY_DEBUG"), "1", StringComparison.Ordinal);
        TimingLogging = string.Equals(Environment.GetEnvironmentVariable("GROCERY_TIMING"), "1", StringComparison.Ordinal);

        _wsUrl = Environment.GetEnvironmentVariable("GROCERY_WS_URL");

        if (string.IsNullOrEmpty(_wsUrl))
        {
            Console.WriteLine("url must not be empty.");
            return;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("Connecting to websocket");

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
                var receivedAt = Stopwatch.GetTimestamp();

                LogDebug($"received: {state}");
                if (DebugLogging)
                {
                    LogStateDelta(LastObservedState, state, LastPayload);
                    LastObservedState = state;
                }

                if (state.Type == "game_over")
                {
                    ResetGameTracking();
                    Console.WriteLine($"game over! Score: {state.Score}");
                    break;
                }

                if (string.Equals(state.ActionStatus, "timeout", StringComparison.OrdinalIgnoreCase))
                {
                    HandleTimeout(state, receivedAt);
                }

                if (state.Bots.Count == 0)
                {
                    Console.WriteLine("Bots is empty or null");
                    break;
                }

                var roundTimer = Stopwatch.StartNew();
                var buildStart = Stopwatch.GetTimestamp();
                var decision = new BotResponse
                {
                    Actions = BuildActions(state)
                };
                var buildEnd = Stopwatch.GetTimestamp();
                LogDebug($"decision: {decision}");
                LogDebug(string.Empty);
                var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(decision, JsonOptions);
                var serializeEnd = Stopwatch.GetTimestamp();
                LogDebug($"payload_bytes: {payloadBytes.Length}");
                if (DebugLogging)
                {
                    LastPayload = Encoding.UTF8.GetString(payloadBytes);
                }

                await _socket.SendAsync(
                    payloadBytes,
                    WebSocketMessageType.Text,
                    true,
                    cts.Token
                );
                var sendEnd = Stopwatch.GetTimestamp();

                roundTimer.Stop();
                LastRoundTiming =
                    $"round_timing: round={state.Round} receive_to_build_ms={GetElapsedMilliseconds(receivedAt, buildStart)} build_ms={GetElapsedMilliseconds(buildStart, buildEnd)} serialize_ms={GetElapsedMilliseconds(buildEnd, serializeEnd)} send_ms={GetElapsedMilliseconds(serializeEnd, sendEnd)} total_ms={roundTimer.ElapsedMilliseconds}";

                if (TimingLogging && roundTimer.ElapsedMilliseconds >= 1500)
                {
                    if (!string.IsNullOrEmpty(LastPlannerTiming))
                    {
                        Console.WriteLine(LastPlannerTiming);
                    }

                    Console.WriteLine(LastRoundTiming);
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

    private static void HandleTimeout(GameState state, long receivedAt)
    {
        Console.WriteLine(
            $"warning: server reported timeout for round {state.Round - 1}; received_at_utc={DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)} receive_gap_ms={GetElapsedMilliseconds(receivedAt, Stopwatch.GetTimestamp())} gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}; clearing transient planner state");
        if (!string.IsNullOrEmpty(LastTaskSummary))
        {
            Console.WriteLine($"task_summary: {LastTaskSummary}");
        }
        RecoveryRoundsRemaining = RecoveryWindowRounds;
        if (TimingLogging)
        {
            if (!string.IsNullOrEmpty(LastPlannerTiming))
            {
                Console.WriteLine(LastPlannerTiming);
            }

            if (!string.IsNullOrEmpty(LastRoundTiming))
            {
                Console.WriteLine(LastRoundTiming);
            }
        }

        LastPlannedMoves.Clear();
        LastActionAttempts.Clear();
        CourierRoutes.Clear();
        CourierTargets.Clear();
        PickupAccessTargets.Clear();
        CurrentRoundCache = null;
    }

    private static void ResetGameTracking()
    {
        LastPositions.Clear();
        StuckRounds.Clear();
        LastPlannedMoves.Clear();
        LastActionAttempts.Clear();
        CourierRoutes.Clear();
        PickupAccessTargets.Clear();
        CourierTargets.Clear();
        ParkingTargets.Clear();
        KnownShelfCells.Clear();
        CurrentRoundCache = null;
        LastTaskSummary = null;
        RecoveryRoundsRemaining = 0;
    }

    private static async Task<GameState> Receive(CancellationToken ct)
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
                if (DebugLogging)
                {
                    Console.WriteLine($"Round: {mappedResult.Round}");
                }
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

    private static long GetElapsedMilliseconds(long startTimestamp, long endTimestamp)
    {
        return (endTimestamp - startTimestamp) * 1000 / Stopwatch.Frequency;
    }
}
