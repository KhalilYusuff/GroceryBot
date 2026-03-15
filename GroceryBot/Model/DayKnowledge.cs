namespace GroceryBot.Model;

public class DayKnowledge
{
    public string DayKey { get; set; } = string.Empty;
    public string? MapSignature { get; set; }
    public List<KnownOrderRecord> Orders { get; set; } = [];
    public List<KnownItemRecord> Items { get; set; } = [];
    public List<RoundScoreRecord> Scores { get; set; } = [];
}

public class KnownOrderRecord
{
    public string OrderId { get; set; } = string.Empty;
    public List<string> ItemsRequired { get; set; } = [];
    public Dictionary<string, int> RequiredCounts { get; set; } = [];
    public int FirstSeenRound { get; set; }
    public int? FirstPreviewRound { get; set; }
    public int? FirstActiveRound { get; set; }
}

public class KnownItemRecord
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int FirstSeenRound { get; set; }
    public int LastSeenRound { get; set; }
}

public class RoundScoreRecord
{
    public int Round { get; set; }
    public int Score { get; set; }
}
