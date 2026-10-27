using System.Collections.Concurrent;
using Mint.Drops;
using Mint.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Mint.IntegrationTests;

public class DropsConcurrencyTests
{
    [Fact]
    public async Task LastUnit_TwoConcurrentOrders_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var drop = await TestDatabase.AddDropAsync(unitsTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Order, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var orderId = Guid.CreateVersion7();
            await using var db = TestDatabase.Drops();
            var outcome = await new DropsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveUnits(orderId, Guid.NewGuid(), drop.Id, 1, $"COL-{n}"));
            outcomes.Add((orderId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Drops();
        var remaining = await check.Drops.Where(s => s.Id == drop.Id).Select(s => s.UnitsLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Order;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.OrderId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheUnitsBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var drop = await TestDatabase.AddDropAsync(unitsTotal: 4);
        var orderId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Drops())
            await new DropsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveUnits(orderId, Guid.NewGuid(), drop.Id, 3, "COL-1"));

        // Two different commands (poison compensation and a refund) both ask for the same units back.
        await using var first = TestDatabase.Drops();
        await using var second = TestDatabase.Drops();
        var one = await new DropsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseUnits(orderId, "poison"));
        var two = await new DropsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseUnits(orderId, "refund"));

        await using var check = TestDatabase.Drops();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Drops.Where(s => s.Id == drop.Id).Select(s => s.UnitsLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var drop = await TestDatabase.AddDropAsync(unitsTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveUnits(Guid.CreateVersion7(), Guid.NewGuid(), drop.Id, 2, "COL-1");

        await using var a = TestDatabase.Drops();
        await using var b = TestDatabase.Drops();
        var first = await new DropsHandler(a).ReserveAsync(messageId, command);
        var second = await new DropsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Drops();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Drops.Where(s => s.Id == drop.Id).Select(s => s.UnitsLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var drop = await TestDatabase.AddDropAsync(unitsTotal: 2);
        await using var db = TestDatabase.Drops();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Drops.Where(s => s.Id == drop.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UnitsLeft, x => x.UnitsLeft - 3)));
    }

    [Fact]
    public async Task Cap_ConcurrentOrdersByTheSameCollector_NeverPassTheCap()
    {
        await TestDatabase.OpenAsync();
        var drop = await TestDatabase.AddDropAsync(unitsTotal: 20, maxPerCollector: 2);
        var collector = Guid.NewGuid();

        var outcomes = new ConcurrentBag<ReserveOutcome>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = TestDatabase.Drops();
            outcomes.Add(await new DropsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveUnits(Guid.CreateVersion7(), collector, drop.Id, 1, $"COL-{n}")));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Drops();
        Assert.Equal(2, outcomes.Count(o => o == ReserveOutcome.Held));
        Assert.Equal(4, outcomes.Count(o => o == ReserveOutcome.CapReached));
        Assert.Equal(2, await check.Collectors.Where(c => c.OwnerId == collector).Select(c => c.Units).SingleAsync());
        Assert.Equal(18, await check.Drops.Where(s => s.Id == drop.Id).Select(s => s.UnitsLeft).FirstAsync());
        Assert.Equal(2, await check.Holds.CountAsync());
    }

    [Fact]
    public async Task Cap_AtWholeRequestLevel_RefusesTheOrderThatWouldCrossIt()
    {
        await TestDatabase.OpenAsync();
        var drop = await TestDatabase.AddDropAsync(unitsTotal: 10, maxPerCollector: 2);
        var collector = Guid.NewGuid();
        await using var db = TestDatabase.Drops();
        var handler = new DropsHandler(db);

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveUnits(Guid.CreateVersion7(), collector, drop.Id, 1, "COL-1")));
        // 1 held + 2 asked = 3 > 2: nothing moves, neither the stock nor the collector's count
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveUnits(Guid.CreateVersion7(), collector, drop.Id, 2, "COL-2")));
        // another collector is unaffected
        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveUnits(Guid.CreateVersion7(), Guid.NewGuid(), drop.Id, 2, "COL-3")));

        await using var check = TestDatabase.Drops();
        Assert.Equal(1, await check.Collectors.Where(c => c.OwnerId == collector).Select(c => c.Units).SingleAsync());
        Assert.Equal(7, await check.Drops.Where(s => s.Id == drop.Id).Select(s => s.UnitsLeft).FirstAsync());
        var refusal = await check.Outbox.Where(o => o.RoutingKey == Topology.ShortKey).ToListAsync();
        Assert.Single(refusal);
        Assert.Contains("\"cap\"", refusal[0].Payload);
    }

    [Fact]
    public async Task Cap_StockShortAfterTheCapPassed_RollsTheCapBack()
    {
        await TestDatabase.OpenAsync();
        var drop = await TestDatabase.AddDropAsync(unitsTotal: 1, maxPerCollector: 2);
        var collector = Guid.NewGuid();
        await using var db = TestDatabase.Drops();

        var outcome = await new DropsHandler(db).ReserveAsync(
            Guid.CreateVersion7(), new ReserveUnits(Guid.CreateVersion7(), collector, drop.Id, 2, "COL-1"));

        await using var check = TestDatabase.Drops();
        Assert.Equal(ReserveOutcome.Short, outcome);
        Assert.False(await check.Collectors.AnyAsync(c => c.OwnerId == collector));
        Assert.Equal(1, await check.Drops.Where(s => s.Id == drop.Id).Select(s => s.UnitsLeft).FirstAsync());
    }

    [Fact]
    public async Task Cap_ReleaseGivesTheCollectorTheirAllowanceBack()
    {
        await TestDatabase.OpenAsync();
        var drop = await TestDatabase.AddDropAsync(unitsTotal: 10, maxPerCollector: 2);
        var collector = Guid.NewGuid();
        var first = Guid.CreateVersion7();
        await using var db = TestDatabase.Drops();
        var handler = new DropsHandler(db);
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveUnits(first, collector, drop.Id, 2, "COL-1"));
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveUnits(Guid.CreateVersion7(), collector, drop.Id, 1, "COL-2")));

        Assert.True(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleaseUnits(first, "refund")));
        Assert.False(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleaseUnits(first, "refund again")));

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveUnits(Guid.CreateVersion7(), collector, drop.Id, 2, "COL-3")));
        await using var check = TestDatabase.Drops();
        Assert.Equal(2, await check.Collectors.Where(c => c.OwnerId == collector).Select(c => c.Units).SingleAsync());
    }
}
