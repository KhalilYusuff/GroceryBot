using System.Collections.Generic;

public class GameState
{
    public string Type { get; set; }
    public int Round { get; set; }
    public int Max_rounds { get; set; }
    public Grid Grid { get; set; }
    public List<Bot> Bots { get; set; }
    public List<Item> Items { get; set; }
    public List<Order> Orders { get; set; }
    public int[] Drop_off { get; set; }
    public List<int[]> Drop_off_zones { get; set; }
    public int Score { get; set; }
}

public class Grid
{
    public int Width { get; set; }
    public int Height { get; set; }
    public List<int[]> Walls { get; set; }
}

public class Bot
{
    public int Id { get; set; }
    public int[] Position { get; set; }
    public List<string> Inventory { get; set; }
}

public class Item
{
    public string Id { get; set; }
    public string Type { get; set; }
    public int[] Position { get; set; }
}

public class Order
{
    public string Id { get; set; }
    public List<string> Items_required { get; set; }
    public List<string> Items_delivered { get; set; }
    public bool Complete { get; set; }
    public string Status { get; set; }
}