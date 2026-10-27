namespace Mint.Drops;

public class Drop
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Series { get; set; } = "";
    public string Artist { get; set; } = "";
    public DateTimeOffset ReleaseDate { get; set; }
    public long PriceCents { get; set; }
    public int UnitsTotal { get; set; }
    public int UnitsLeft { get; set; }
    /// <summary>The most units one collector may hold in this drop at any time.</summary>
    public int MaxPerCollector { get; set; } = 2;
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class UnitHold
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DropId { get; set; }
    public int Units { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>How many units one collector holds in one drop. Written only by the Drops handler.</summary>
public class DropCollector
{
    public Guid DropId { get; set; }
    public Guid OwnerId { get; set; }
    public int Units { get; set; }
}
