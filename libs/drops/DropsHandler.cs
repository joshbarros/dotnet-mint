using Mint.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Mint.Drops;

public enum ReserveOutcome { Held, Short, CapReached, Duplicate }

/// <summary>
/// The only writer of Drop.UnitsLeft and of unit holds.
/// Two atomic steps in one transaction: a per-collector cap upsert that only moves while the new total fits, then one
/// conditional UPDATE that matches only while the remaining Units still covers the request.
/// </summary>
public class DropsHandler(DropsDbContext db) : IMessageHandler
{
    public const string Name = "drops";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveUnits>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseUnits>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Drops does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveUnits command, CancellationToken ct = default)
    {
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await db.ClaimAsync(Name, messageId, ct))
            {
                await tx.CommitAsync(ct);
                return ReserveOutcome.Duplicate;
            }

            // 1. the per-collector cap: one atomic upsert that only moves while the new total still fits
            var withinCap = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO drops."DropCollectors" ("DropId", "OwnerId", "Units")
                SELECT d."Id", {command.OwnerId}, {command.Units} FROM drops."Drops" d
                WHERE d."Id" = {command.DropId} AND d."MaxPerCollector" >= {command.Units}
                ON CONFLICT ("DropId", "OwnerId") DO UPDATE SET "Units" = drops."DropCollectors"."Units" + {command.Units}
                WHERE drops."DropCollectors"."Units" + {command.Units} <=
                      (SELECT "MaxPerCollector" FROM drops."Drops" WHERE "Id" = {command.DropId})
                """, ct);

            // 2. the remaining units: one conditional UPDATE, the only writer of UnitsLeft
            var updated = withinCap == 1
                ? await db.Drops
                    .Where(s => s.Id == command.DropId && s.UnitsLeft >= command.Units)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UnitsLeft, x => x.UnitsLeft - command.Units), ct)
                : 0;

            if (withinCap == 1 && updated == 1)
            {
                db.Holds.Add(new UnitHold
                {
                    OrderId = command.OrderId,
                    OwnerId = command.OwnerId,
                    DropId = command.DropId,
                    Units = command.Units,
                    Status = HoldStatus.Held,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                db.Enqueue("drops", Topology.HeldKey, command.OrderId,
                    new UnitsHeld(command.OrderId, command.OwnerId, command.DropId, command.Units, command.Reference));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ReserveOutcome.Held;
            }

            // Either the cap or the stock said no. Roll back, so a cap increment never outlives a failed reserve.
            await tx.RollbackAsync(ct);
        }

        // The refusal is recorded in a fresh transaction (the claim was rolled back with the attempt).
        db.ChangeTracker.Clear();
        await using var refusal = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await refusal.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var drop = await db.Drops.AsNoTracking().Where(s => s.Id == command.DropId)
            .Select(s => new { s.UnitsLeft, s.MaxPerCollector }).FirstOrDefaultAsync(ct);
        var held = await db.Collectors.AsNoTracking()
            .Where(c => c.DropId == command.DropId && c.OwnerId == command.OwnerId)
            .Select(c => c.Units).FirstOrDefaultAsync(ct);
        var overCap = drop is not null && held + command.Units > drop.MaxPerCollector;
        db.Enqueue("drops", Topology.ShortKey, command.OrderId,
            new UnitsShort(command.OrderId, command.Reference, command.Units, drop?.UnitsLeft ?? 0,
                overCap ? "cap" : "stock", drop?.MaxPerCollector ?? 0));
        await db.SaveChangesAsync(ct);
        await refusal.CommitAsync(ct);
        return overCap ? ReserveOutcome.CapReached : ReserveOutcome.Short;
    }

    /// <summary>Gives held units back exactly once. A second release for the same order changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseUnits command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.OrderId == command.OrderId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.OrderId == command.OrderId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Drops
                    .Where(s => s.Id == hold.DropId && s.UnitsLeft + hold.Units <= s.UnitsTotal)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UnitsLeft, x => x.UnitsLeft + hold.Units), ct);
                await db.Collectors
                    .Where(c => c.DropId == hold.DropId && c.OwnerId == hold.OwnerId && c.Units >= hold.Units)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Units, x => x.Units - hold.Units), ct);
                db.Enqueue("drops", Topology.ReleasedKey, command.OrderId,
                    new UnitsReleased(command.OrderId, hold.Units, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
