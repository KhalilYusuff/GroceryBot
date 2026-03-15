using GroceryBot.Model;
using System.Text.Json;

namespace GroceryBot;

public partial class GroceryBotClient
{
    private static void InitializeDayKnowledge()
    {
        var rootDirectory = ResolveWorkspaceRoot();
        var dataDirectory = Path.Combine(rootDirectory, "GroceryBot", "Data");
        Directory.CreateDirectory(dataDirectory);

        var dayKey = DateTime.Now.ToString("yyyy-MM-dd");
        DayKnowledgePath = Path.Combine(dataDirectory, $"dayknowledge-{dayKey}.json");
        CurrentDayKnowledge = LoadDayKnowledge(DayKnowledgePath, dayKey);
        DayKnowledgeDirty = CurrentDayKnowledge != null && NormalizeDayKnowledgeOrders(CurrentDayKnowledge);
        SaveDayKnowledgeIfDirty();
    }

    private static void ObserveDayKnowledge(GameState state)
    {
        if (CurrentDayKnowledge == null)
        {
            return;
        }

        var mapSignature = $"{state.Grid.Width}x{state.Grid.Height}:{state.Bots.Count}:{state.Max_rounds}";
        if (string.IsNullOrEmpty(CurrentDayKnowledge.MapSignature))
        {
            CurrentDayKnowledge.MapSignature = mapSignature;
            DayKnowledgeDirty = true;
        }

        foreach (var order in state.Orders)
        {
            var requiredCounts = BuildRequiredCounts(order.Items_required);
            var existing = CurrentDayKnowledge.Orders
                .FirstOrDefault(candidate => string.Equals(candidate.OrderId, order.Id, StringComparison.Ordinal));
            if (existing == null)
            {
                existing = new KnownOrderRecord
                {
                    OrderId = order.Id,
                    ItemsRequired = [.. order.Items_required],
                    RequiredCounts = requiredCounts,
                    FirstSeenRound = state.Round,
                    FirstPreviewRound = string.Equals(order.Status, "preview", StringComparison.Ordinal) ? state.Round : null,
                    FirstActiveRound = string.Equals(order.Status, "active", StringComparison.Ordinal) ? state.Round : null
                };
                CurrentDayKnowledge.Orders.Add(existing);
                DayKnowledgeDirty = true;
            }
            else
            {
                if (existing.ItemsRequired.Count == 0 && order.Items_required.Count > 0)
                {
                    existing.ItemsRequired = [.. order.Items_required];
                    DayKnowledgeDirty = true;
                }

                if (existing.RequiredCounts.Count == 0 && requiredCounts.Count > 0)
                {
                    existing.RequiredCounts = requiredCounts;
                    DayKnowledgeDirty = true;
                }

                if (string.Equals(order.Status, "preview", StringComparison.Ordinal) && existing.FirstPreviewRound == null)
                {
                    existing.FirstPreviewRound = state.Round;
                    DayKnowledgeDirty = true;
                }

                if (string.Equals(order.Status, "active", StringComparison.Ordinal) && existing.FirstActiveRound == null)
                {
                    existing.FirstActiveRound = state.Round;
                    DayKnowledgeDirty = true;
                }
            }
        }

        foreach (var item in state.Items.Where(candidate => candidate.Position.Length >= 2))
        {
            if (CurrentDayKnowledge.Items.Any(candidate => string.Equals(candidate.ItemId, item.Id, StringComparison.Ordinal)))
            {
                var existing = CurrentDayKnowledge.Items
                    .First(candidate => string.Equals(candidate.ItemId, item.Id, StringComparison.Ordinal));
                if (existing.LastSeenRound != state.Round)
                {
                    existing.LastSeenRound = state.Round;
                    DayKnowledgeDirty = true;
                }
                continue;
            }

            CurrentDayKnowledge.Items.Add(new KnownItemRecord
            {
                ItemId = item.Id,
                ItemType = item.Type,
                X = item.Position[0],
                Y = item.Position[1],
                FirstSeenRound = state.Round,
                LastSeenRound = state.Round
            });
            DayKnowledgeDirty = true;
        }

        if (!CurrentDayKnowledge.Scores.Any(candidate => candidate.Round == state.Round))
        {
            CurrentDayKnowledge.Scores.Add(new RoundScoreRecord
            {
                Round = state.Round,
                Score = state.Score
            });
            DayKnowledgeDirty = true;
        }

        if (NormalizeDayKnowledgeOrders(CurrentDayKnowledge))
        {
            DayKnowledgeDirty = true;
        }

        SaveDayKnowledgeIfDirty();
    }

    private static (Dictionary<string, double> WeightedDemand, List<KnownOrderRecord> NextOrders, string Confidence) BuildStrategicDemandProfile(GameState state)
    {
        var weightedDemand = new Dictionary<string, double>(StringComparer.Ordinal);
        var active = state.Orders.FirstOrDefault(order => string.Equals(order.Status, "active", StringComparison.Ordinal));
        var preview = state.Orders.FirstOrDefault(order => string.Equals(order.Status, "preview", StringComparison.Ordinal));

        void AddWeightedDemand(IEnumerable<string> itemTypes, double weight)
        {
            foreach (var itemType in itemTypes)
            {
                weightedDemand[itemType] = weightedDemand.GetValueOrDefault(itemType) + weight;
            }
        }

        if (active != null)
        {
            AddWeightedDemand(GetOutstandingNeeded(active), 1.0);
        }

        if (preview != null)
        {
            AddWeightedDemand(GetOutstandingNeeded(preview), 0.5);
        }

        var plannedTimeline = BuildPlannedDemandTimeline(state, active?.Id, 2);
        var nextOrders = plannedTimeline
            .Select(entry => CurrentDayKnowledge?.Orders.FirstOrDefault(order => string.Equals(order.OrderId, entry.OrderId, StringComparison.Ordinal)))
            .Where(order => order != null)
            .Cast<KnownOrderRecord>()
            .ToList();
        if (plannedTimeline.Count > 0)
        {
            AddWeightedDemand(ExpandCounts(plannedTimeline[0].RequiredCounts), 0.35);
        }

        if (plannedTimeline.Count > 1)
        {
            AddWeightedDemand(ExpandCounts(plannedTimeline[1].RequiredCounts), 0.15);
        }

        var confidence = plannedTimeline.Count >= 2
            ? "high"
            : plannedTimeline.Count == 1
                ? "partial"
                : "low";
        return (weightedDemand, nextOrders, confidence);
    }

    private static Dictionary<string, int> BuildFutureDemandCounts(GameState state, Dictionary<string, int> activeDemand)
    {
        var strategicProfile = BuildStrategicDemandProfile(state);
        return strategicProfile.WeightedDemand
            .Where(pair => pair.Value >= 0.5)
            .Where(pair => activeDemand.GetValueOrDefault(pair.Key) <= 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(4)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value >= 1.5 ? 2 : 1,
                StringComparer.Ordinal);
    }

    private static List<KnownOrderRecord> GetKnownNextOrders(GameState state, string? activeOrderId, int maxOrders)
    {
        var timeline = BuildPlannedDemandTimeline(state, activeOrderId, maxOrders);
        if (CurrentDayKnowledge == null)
        {
            return [];
        }

        return timeline
            .Select(entry => CurrentDayKnowledge.Orders.FirstOrDefault(order => string.Equals(order.OrderId, entry.OrderId, StringComparison.Ordinal)))
            .Where(order => order != null)
            .Cast<KnownOrderRecord>()
            .ToList();
    }

    private static List<PlannedOrderTimelineEntry> BuildPlannedDemandTimeline(GameState state, string? activeOrderId, int maxOrders)
    {
        if (CurrentDayKnowledge == null || string.IsNullOrEmpty(activeOrderId) || maxOrders <= 0)
        {
            return [];
        }

        var knownActiveOrders = GetCanonicalKnownOrders(CurrentDayKnowledge)
            .Where(order => order.FirstActiveRound != null || order.FirstPreviewRound != null || order.FirstSeenRound >= 0)
            .ToList();
        var currentIndex = knownActiveOrders.FindIndex(order => string.Equals(order.OrderId, activeOrderId, StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            return [];
        }

        return knownActiveOrders
            .Skip(currentIndex + 1)
            .Take(maxOrders)
            .Select(order => new PlannedOrderTimelineEntry
            {
                OrderId = order.OrderId,
                SequenceIndex = GetOrderSequenceIndex(order.OrderId),
                FirstPreviewRound = order.FirstPreviewRound,
                FirstActiveRound = order.FirstActiveRound,
                RequiredCounts = GetKnownRequiredCounts(order)
            })
            .ToList();
    }

    private static StrategicPrefetchPlan BuildStrategicPrefetchPlan(GameState state, Dictionary<string, int> activeDemand)
    {
        var active = state.Orders.FirstOrDefault(order => string.Equals(order.Status, "active", StringComparison.Ordinal));
        var activeOutstanding = activeDemand.Values.Where(count => count > 0).Sum();
        var timeline = BuildPlannedDemandTimeline(state, active?.Id, 2);
        if (timeline.Count == 0)
        {
            return new StrategicPrefetchPlan();
        }

        var budgets = new Dictionary<string, int>(StringComparer.Ordinal);
        AddDemandCounts(budgets, timeline[0].RequiredCounts, 1.0);
        if (timeline.Count > 1)
        {
            AddDemandCounts(budgets, timeline[1].RequiredCounts, 0.5);
        }

        foreach (var activeType in activeDemand.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToList())
        {
            budgets.Remove(activeType);
        }

        foreach (var bot in state.Bots)
        {
            foreach (var carried in bot.Inventory)
            {
                if (budgets.GetValueOrDefault(carried) <= 0)
                {
                    continue;
                }

                budgets[carried]--;
            }
        }

        budgets = budgets
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        return new StrategicPrefetchPlan
        {
            TargetOrderId = timeline[0].OrderId,
            Budgets = budgets,
            Timeline = timeline,
            Enabled = activeOutstanding <= 1 && budgets.Count > 0
        };
    }

    private static void UpdateStrategicSummaries(GameState state)
    {
        var strategicProfile = BuildStrategicDemandProfile(state);
        var active = state.Orders.FirstOrDefault(order => string.Equals(order.Status, "active", StringComparison.Ordinal));
        var activeDemand = active == null ? new Dictionary<string, int>() : GetOutstandingCounts(active);
        var prefetchPlan = BuildStrategicPrefetchPlan(state, activeDemand);
        LastFutureDemandSummary = string.Join(
            ",",
            strategicProfile.WeightedDemand
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value:0.00}"));
        LastKnownNextOrdersSummary = strategicProfile.NextOrders.Count == 0
            ? "-"
            : string.Join(
                " | ",
                strategicProfile.NextOrders.Select(order =>
                    $"{order.OrderId}:[{string.Join(",", ExpandCounts(GetKnownRequiredCounts(order)).OrderBy(item => item, StringComparer.Ordinal))}]"));
        LastPlannedTimelineSummary = prefetchPlan.Timeline.Count == 0
            ? "-"
            : string.Join(
                " | ",
                prefetchPlan.Timeline.Select(entry =>
                    $"{entry.OrderId}@p{entry.FirstPreviewRound?.ToString() ?? "-"}@a{entry.FirstActiveRound?.ToString() ?? "-"}:[{string.Join(",", entry.RequiredCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}]"));
        LastPrefetchBudgetSummary = prefetchPlan.Budgets.Count == 0
            ? "-"
            : string.Join(
                ",",
                prefetchPlan.Budgets
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
        LastDayScriptConfidence = strategicProfile.Confidence;
    }

    private static void SaveDayKnowledgeIfDirty()
    {
        if (!DayKnowledgeDirty || CurrentDayKnowledge == null || string.IsNullOrEmpty(DayKnowledgePath))
        {
            return;
        }

        var json = JsonSerializer.Serialize(CurrentDayKnowledge, JsonOptions);
        File.WriteAllText(DayKnowledgePath, json);
        DayKnowledgeDirty = false;
    }

    private static DayKnowledge LoadDayKnowledge(string path, string dayKey)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<DayKnowledge>(File.ReadAllText(path), JsonOptions);
                if (loaded != null)
                {
                    loaded.DayKey = string.IsNullOrEmpty(loaded.DayKey) ? dayKey : loaded.DayKey;
                    NormalizeDayKnowledgeOrders(loaded);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            LogLine($"warning: failed to load day knowledge: {ex.Message}");
        }

        return new DayKnowledge
        {
            DayKey = dayKey
        };
    }

    private static string ResolveWorkspaceRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GroceryBot.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static List<KnownOrderRecord> GetCanonicalKnownOrders(DayKnowledge knowledge)
    {
        return knowledge.Orders
            .OrderBy(order => GetOrderSequenceIndex(order.OrderId))
            .ThenBy(order => order.FirstPreviewRound ?? int.MaxValue)
            .ThenBy(order => order.FirstActiveRound ?? int.MaxValue)
            .ThenBy(order => order.FirstSeenRound)
            .ThenBy(order => order.OrderId, StringComparer.Ordinal)
            .ToList();
    }

    private static bool NormalizeDayKnowledgeOrders(DayKnowledge knowledge)
    {
        var changed = false;
        var ordered = GetCanonicalKnownOrders(knowledge);
        int? previousPreviewRound = null;
        int? previousActiveRound = null;

        foreach (var order in ordered)
        {
            if (previousPreviewRound != null && order.FirstPreviewRound != null && order.FirstPreviewRound < previousPreviewRound)
            {
                order.FirstPreviewRound = previousPreviewRound;
                changed = true;
            }

            if (previousActiveRound != null && order.FirstActiveRound != null && order.FirstActiveRound < previousActiveRound)
            {
                order.FirstActiveRound = previousActiveRound;
                changed = true;
            }

            if (order.FirstActiveRound != null && order.FirstPreviewRound != null && order.FirstActiveRound < order.FirstPreviewRound)
            {
                order.FirstActiveRound = order.FirstPreviewRound;
                changed = true;
            }

            var normalizedCounts = BuildRequiredCounts(order.ItemsRequired);
            if (order.RequiredCounts.Count == 0 && normalizedCounts.Count > 0)
            {
                order.RequiredCounts = normalizedCounts;
                changed = true;
            }

            previousPreviewRound = order.FirstPreviewRound ?? previousPreviewRound;
            previousActiveRound = order.FirstActiveRound ?? previousActiveRound;
        }

        return changed;
    }

    private static int GetOrderSequenceIndex(string? orderId)
    {
        if (string.IsNullOrEmpty(orderId))
        {
            return int.MaxValue;
        }

        const string prefix = "order_";
        if (!orderId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return int.MaxValue;
        }

        return int.TryParse(orderId[prefix.Length..], out var index)
            ? index
            : int.MaxValue;
    }

    private static Dictionary<string, int> BuildRequiredCounts(IEnumerable<string> itemTypes)
    {
        return itemTypes
            .GroupBy(itemType => itemType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    private static Dictionary<string, int> GetKnownRequiredCounts(KnownOrderRecord order)
    {
        return order.RequiredCounts.Count > 0
            ? new Dictionary<string, int>(order.RequiredCounts, StringComparer.Ordinal)
            : BuildRequiredCounts(order.ItemsRequired);
    }

    private static IEnumerable<string> ExpandCounts(Dictionary<string, int> counts)
    {
        foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            for (var i = 0; i < pair.Value; i++)
            {
                yield return pair.Key;
            }
        }
    }

    private static void AddDemandCounts(Dictionary<string, int> target, Dictionary<string, int> source, double weight)
    {
        foreach (var pair in source)
        {
            var weightedCount = Math.Max(0, (int)Math.Ceiling(pair.Value * weight));
            if (weightedCount <= 0)
            {
                continue;
            }

            target[pair.Key] = target.GetValueOrDefault(pair.Key) + weightedCount;
        }
    }
}
