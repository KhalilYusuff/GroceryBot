namespace GroceryBot.Model;

public class BotResponse
{
    public Actions[] Actions { get; set; }

    public override string ToString()
    {
        var actions = Actions == null ? "null" : $"[{string.Join(", ", Actions)}]";
        return $"BotResponse(Actions={actions})";
    }
}

public class Actions
{
    public int BotId { get; set; }
    public string Action { get; set; }
    public string ItemId { get; set; }

    public override string ToString()
    {
        return $"Action(BotId={BotId}, Action={Action}, ItemId={ItemId})";
    }
}
