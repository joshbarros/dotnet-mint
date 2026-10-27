namespace Mint.Orders;

public enum OrderStatus { Requested, Held, Sold, Refunded, Rejected }

public class Order
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DropId { get; set; }
    public string DropCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Units { get; set; }
    public long TotalCents { get; set; }
    public OrderStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? CertificateNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? UnitsReleasedAt { get; set; }
}

public class OrderOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the refund race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateOrderRequest(string? DropCode, string? Reference, long Units);

public record OrderDto(
    Guid Id, Guid OwnerId, string DropCode, string Reference, int Units, long TotalCents, string Status,
    string? Reason, string? CertificateNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? UnitsReleasedAt)
{
    public static OrderDto From(Order b) => new(
        b.Id, b.OwnerId, b.DropCode, b.Reference, b.Units, b.TotalCents, b.Status.ToString(),
        b.Reason, b.CertificateNumber, b.CreatedAt, b.UpdatedAt, b.UnitsReleasedAt);
}
