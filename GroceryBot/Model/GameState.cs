using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;

public class GameState
{
    public string Type { get; set; } = string.Empty;
    public int Round { get; set; }

    [JsonPropertyName("max_rounds")]
    public int Max_rounds { get; set; }

    public Grid Grid { get; set; } = new();
    public List<Bot> Bots { get; set; } = [];
    public List<Item> Items { get; set; } = [];
    public List<Order> Orders { get; set; } = [];

    [JsonPropertyName("drop_off")]
    public int[] Drop_off { get; set; } = [];

    [JsonPropertyName("drop_off_zones")]
    public List<int[]> Drop_off_zones { get; set; } = [];

    public int Score { get; set; }

    public override string ToString()
    {
        var dropOff = Drop_off == null ? "null" : $"[{string.Join(", ", Drop_off)}]";
        var dropOffZones = Drop_off_zones == null
            ? "null"
            : $"[{string.Join(", ", Drop_off_zones.Select(FormatPoint))}]";

        return $"GameState(Type={Type}, Round={Round}, MaxRounds={Max_rounds}, Grid={Grid}, " +
               $"Bots=[{JoinOrEmpty(Bots)}], Items=[{JoinOrEmpty(Items)}], Orders=[{JoinOrEmpty(Orders)}], " +
               $"DropOff={dropOff}, DropOffZones={dropOffZones}, Score={Score})";
    }

    private static string FormatPoint(int[]? point)
    {
        return point == null ? "null" : $"[{string.Join(", ", point)}]";
    }

    private static string JoinOrEmpty<T>(IEnumerable<T>? items)
    {
        return items == null ? string.Empty : string.Join(", ", items);
    }
}

public class Grid
{
    public int Width { get; set; }
    public int Height { get; set; }
    public List<int[]> Walls { get; set; } = [];

    public override string ToString()
    {
        var walls = Walls == null ? "null" : $"[{string.Join(", ", Walls.Select(w => w == null ? "null" : $"[{string.Join(", ", w)}]"))}]";
        return $"Grid(Width={Width}, Height={Height}, Walls={walls})";
    }
}

public class Bot
{
    public int Id { get; set; }
    public int[] Position { get; set; } = [];
    public List<string> Inventory { get; set; } = [];

    public override string ToString()
    {
        var position = Position == null ? "null" : $"[{string.Join(", ", Position)}]";
        var inventory = Inventory == null ? "null" : $"[{string.Join(", ", Inventory)}]";
        return $"Bot(Id={Id}, Position={position}, Inventory={inventory})";
    }
}

public class Item
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int[] Position { get; set; } = [];

    public override string ToString()
    {
        var position = Position == null ? "null" : $"[{string.Join(", ", Position)}]";
        return $"Item(Id={Id}, Type={Type}, Position={position})";
    }
}

public class Order
{
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("items_required")]
    public List<string> Items_required { get; set; } = [];

    [JsonPropertyName("items_delivered")]
    public List<string> Items_delivered { get; set; } = [];

    public bool Complete { get; set; }
    public string Status { get; set; } = string.Empty;

    public override string ToString()
    {
        var required = Items_required == null ? "null" : $"[{string.Join(", ", Items_required)}]";
        var delivered = Items_delivered == null ? "null" : $"[{string.Join(", ", Items_delivered)}]";
        return $"Order(Id={Id}, Required={required}, Delivered={delivered}, Complete={Complete}, Status={Status})";
    }
}
