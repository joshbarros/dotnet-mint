using Mint.Orders;
using Mint.Drops;
using Mint.Certificates;
using Mint.Shared.Auth;
using Mint.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Mint.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MINT_TEST_DB")
        ?? "Host=localhost;Port=5481;Database=mint_test;Username=mint;Password=mint";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static OrdersDbContext Orders() => new(Options<OrdersDbContext>(OrdersDbContext.SchemaName));
    public static DropsDbContext Drops() => new(Options<DropsDbContext>(DropsDbContext.SchemaName));
    public static CertificatesDbContext Certificates() => new(Options<CertificatesDbContext>(CertificatesDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var orders = Orders();
        await using var units = Drops();
        await using var certificates = Certificates();
        await orders.Database.MigrateAsync();
        await units.Database.MigrateAsync();
        await certificates.Database.MigrateAsync();
        await orders.Database.ExecuteSqlRawAsync(
            "TRUNCATE orders.\"InboxMessages\", orders.\"OutboxMessages\", orders.\"Orders\", " +
            "drops.\"InboxMessages\", drops.\"OutboxMessages\", drops.\"UnitHolds\", drops.\"DropCollectors\", drops.\"Drops\", " +
            "certificates.\"InboxMessages\", certificates.\"OutboxMessages\", certificates.\"Certificates\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Drop> AddDropAsync(int unitsTotal, long priceCents = 5_000, int? maxPerCollector = null)
    {
        await using var db = Drops();
        var drop = new Drop
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Drop",
            Series = "Genesis",
            Artist = "Studio Aria",
            ReleaseDate = DateTimeOffset.UtcNow.AddDays(7),
            PriceCents = priceCents,
            UnitsTotal = unitsTotal,
            UnitsLeft = unitsTotal,
            MaxPerCollector = maxPerCollector ?? unitsTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Drops.Add(drop);
        await db.SaveChangesAsync();
        return drop;
    }

    public static async Task<Order> AddOrderAsync(Drop drop, string reference, int units, OrderStatus status, Guid? ownerId = null)
    {
        await using var db = Orders();
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            DropId = drop.Id,
            DropCode = drop.Code,
            Reference = reference,
            Units = units,
            TotalCents = OrderRules.TotalCents(drop.PriceCents, units),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Drop[] drops) : IDropCatalog
{
    public Task<DropInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(drops.Where(s => s.Code == code).Select(s => new DropInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefault());
}
