namespace GroceryBot.Model;

using System.Text.Json.Serialization;

public class BotResponse
{
    [JsonPropertyName("actions")]
    public Actions[] Actions { get; set; } = [];

    public override string ToString()
    {
        var actions = Actions == null ? "null" : $"[{string.Join(", ", Actions)}]";
        return $"BotResponse(Actions={actions})";
    }
}

public class Actions
{
    [JsonPropertyName("bot_id")]
    public int BotId { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }

    public override string ToString()
    {
        return $"Action(BotId={BotId}, Action={Action}, ItemId={ItemId})";
    }
}
