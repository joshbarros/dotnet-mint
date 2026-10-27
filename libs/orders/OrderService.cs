using Mint.Shared;
using Mint.Shared.Auth;
using Mint.Shared.Contracts;
using Mint.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mint.Orders;

/// <summary>HTTP side of the saga: writes the order and its first outbox row in one local transaction.</summary>
public class OrderService(OrdersDbContext db, IDropCatalog drops, ICurrentUser user)
{
    public async Task<Order> CreateAsync(CreateOrderRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.DropCode))
            throw new DomainException(400, "Drop code is required.");
        var error = OrderRules.Validate(request.Reference, request.Units);
        if (error is not null)
            throw new DomainException(400, error);

        var drop = await drops.FindAsync(request.DropCode.Trim(), ct)
            ?? throw new DomainException(404, $"Drop '{request.DropCode}' does not exist.");

        var units = (int)request.Units;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
            DropId = drop.Id,
            DropCode = drop.Code,
            Reference = reference,
            Units = units,
            TotalCents = OrderRules.TotalCents(drop.PriceCents, units),
            Status = OrderStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Orders.Add(order);
        db.Enqueue("orders", Topology.ReserveKey, order.Id, new ReserveUnits(order.Id, ownerId, drop.Id, units, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {drop.Code}.");
        }
        await tx.CommitAsync(ct);
        return order;
    }

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Order> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (order is null || !OwnerAccess.CanAccess(user, order.OwnerId))
            throw new DomainException(404, "Order not found.");
        return order;
    }

    public async Task<(List<Order> Items, int Total)> ListAsync(string? dropCode, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see orders.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(dropCode))
            query = query.Where(b => b.DropCode == dropCode);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>Moves Held or Sold to Refunded and asks Drops to give the units back. The certificate is not touched.</summary>
    public async Task<Order> RefundAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Orders
            .Where(b => b.Id == id && (b.Status == OrderStatus.Held || b.Status == OrderStatus.Sold))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Refunded)
                .SetProperty(b => b.Reason, "Refunded by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Orders.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"An order in status {current.Status} cannot be refunded.");
        }

        db.Enqueue("orders", Topology.ReleaseKey, id, new ReleaseUnits(id, "refund"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Orders.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
