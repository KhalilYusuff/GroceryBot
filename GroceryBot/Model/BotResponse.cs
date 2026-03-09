namespace GroceryBot.Model;

public class BotResponse
{
    public Actions[] Actions { get; set; }
}

public class Actions
{
    public int BotId { get; set; }
    public string Action { get; set; }
    public string ItemId { get; set; }
    
}