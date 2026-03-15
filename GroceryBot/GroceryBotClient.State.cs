using GroceryBot.Model;
using System.Net.WebSockets;
using System.Text.Json;

namespace GroceryBot;

public partial class GroceryBotClient
{
    private static string? _wsUrl;
    private static readonly ClientWebSocket _socket = new();
    private static readonly Dictionary<int, (int X, int Y)> LastPositions = new();
    private static readonly Dictionary<int, int> StuckRounds = new();
    private static readonly Dictionary<int, (string Action, int X, int Y)> LastPlannedMoves = new();
    private static readonly Dictionary<int, (string Action, string? ItemId)> LastActionAttempts = new();
    private static readonly Dictionary<int, RoleAssignment> RoleAssignments = new();
    private static readonly Dictionary<int, FinishAssignmentSnapshot> LastFinishAssignments = new();
    private static readonly Dictionary<int, Queue<(int X, int Y)>> CourierRoutes = new();
    private static readonly Dictionary<int, (string ItemId, int X, int Y)> PickupAccessTargets = new();
    private static readonly Dictionary<int, PickupProgress> PickupProgressByBotId = new();
    private static readonly Dictionary<int, DeliveryProgress> DeliveryProgressByBotId = new();
    private static readonly Dictionary<int, int[]> CourierTargets = new();
    private static readonly Dictionary<int, int[]> ParkingTargets = new();
    private static readonly HashSet<(int X, int Y)> KnownShelfCells = [];
    private static string? LastPlannerTiming;
    private static string? LastRoundTiming;
    private static string? LastTaskSummary;
    private static string? LastRoleSummary;
    private static string? LastActiveDemandSummary;
    private static string? LastActiveWorldSummary;
    private static string? LastFallbackPickupSummary;
    private static string? LastDeliveryPressureSummary;
    private static string? LastFutureDemandSummary;
    private static string? LastKnownNextOrdersSummary;
    private static string? LastPlannedTimelineSummary;
    private static string? LastPrefetchBudgetSummary;
    private static string? LastDayScriptConfidence;
    private static int LastObservedScore;
    private static int LastTrackedScore;
    private static int ScoreStagnationRounds;
    private static int RecentPickupNoEffectCount;
    private static int RecentDropoffNoEffectCount;
    private static int RecentFallbackPickupAttempts;
    private static int RecentFallbackPickupSuccesses;
    private static int RecentFallbackPickupNoCandidate;
    private static int ActiveOrderTurnovers;
    private static int RecoveryRoundsRemaining;
    private static string? LastTrackedActiveOrderId;
    private static GameState? LastObservedState;
    private static string? LastPayload;
    private static RoundCache? CurrentRoundCache;
    private static DayKnowledge? CurrentDayKnowledge;
    private static string? DayKnowledgePath;
    private static bool DayKnowledgeDirty;
    private static bool DebugLogging;
    private static bool TimingLogging;

    private const int PlanningSoftLimitMs = 1600;
    private const int RecoveryWindowRounds = 12;
    private const int EndgameRounds = 35;
    private const int RoleLockRounds = 10;
    private const int SummaryIntervalRounds = 25;
    private const string PlannerVersion = "role-v5";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class PlanningContext
    {
        public HashSet<(int X, int Y)> OccupiedPositions { get; init; } = [];
        public HashSet<(int X, int Y)> ReservedTargets { get; init; } = [];
        public HashSet<(int X, int Y)> ReservedCorridorCells { get; init; } = [];
        public Dictionary<string, List<string>> CommittedTypesByOrderId { get; init; } = [];
        public HashSet<string> ReservedItemIds { get; init; } = [];
        public Dictionary<(int X, int Y), int> DeliveryReservations { get; init; } = [];
    }

    private sealed class BotTask
    {
        public required string Kind { get; init; }
        public string? OrderId { get; init; }
        public string? ItemId { get; init; }
        public string? ItemType { get; init; }
        public int[]? Target { get; init; }
    }

    private sealed class RoleAssignment
    {
        public required string Role { get; init; }
        public required int LockedUntilRound { get; init; }
        public string? FocusItemType { get; init; }
    }

    private sealed class PickupCandidate
    {
        public required Item Item { get; init; }
        public required int[] AccessTarget { get; init; }
        public required int PickupDistance { get; init; }
        public required int DeliveryDistance { get; init; }
        public required int Score { get; init; }
    }

    private sealed class FinishAssignmentSnapshot
    {
        public required string TaskKind { get; init; }
        public string? ItemId { get; init; }
        public string? ItemType { get; init; }
        public int[]? Target { get; init; }
        public required int ConsecutiveRounds { get; init; }
    }

    private sealed class PickupProgress
    {
        public required string ItemId { get; init; }
        public required int TargetX { get; init; }
        public required int TargetY { get; init; }
        public required int BestDistance { get; init; }
        public required int StagnantRounds { get; init; }
    }

    private sealed class PlannedOrderTimelineEntry
    {
        public required string OrderId { get; init; }
        public required int SequenceIndex { get; init; }
        public int? FirstPreviewRound { get; init; }
        public int? FirstActiveRound { get; init; }
        public required Dictionary<string, int> RequiredCounts { get; init; }
    }

    private sealed class StrategicPrefetchPlan
    {
        public string? TargetOrderId { get; init; }
        public Dictionary<string, int> Budgets { get; init; } = new(StringComparer.Ordinal);
        public List<PlannedOrderTimelineEntry> Timeline { get; init; } = [];
        public bool Enabled { get; init; }
    }

    private sealed class DeliveryProgress
    {
        public required int TargetX { get; init; }
        public required int TargetY { get; init; }
        public required int BestDistance { get; init; }
        public required int StagnantRounds { get; init; }
        public required int LastX { get; init; }
        public required int LastY { get; init; }
        public required int RepeatWindow { get; init; }
    }

    private sealed class RoundCache
    {
        public required HashSet<(int X, int Y)> WallCells { get; init; }
        public required HashSet<(int X, int Y)> ShelfCells { get; init; }
        public Dictionary<(int StartX, int StartY, int TargetX, int TargetY), int> DistanceCache { get; } = [];
        public Dictionary<(int StartX, int StartY, int TargetX, int TargetY), List<(int X, int Y)>> PathCache { get; } = [];
    }
}
