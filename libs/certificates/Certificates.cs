using Mint.Shared;
using Mint.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Mint.Certificates;

public enum CertificateStatus { Issued, Rejected }

public class Certificate
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public CertificateStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class CertificatesDbContext(DbContextOptions<CertificatesDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "certificates";

    public DbSet<Certificate> Certificates => Set<Certificate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Certificate>(e =>
        {
            e.ToTable("Certificates");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the certificate once the units are held. A poison reference is committed as rejected.</summary>
public class CertificatesHandler(CertificatesDbContext db) : IMessageHandler
{
    public const string Name = "certificates";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Certificates does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<UnitsHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, UnitsHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Certificates.Add(new Certificate
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Status = CertificateStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("certificates", Topology.ReleaseKey, held.OrderId, new ReleaseUnits(held.OrderId, "poison"));
            db.Enqueue("certificates", Topology.RejectedKey, held.OrderId, new CertificateRejected(held.OrderId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"MC-{held.OrderId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Certificates.Add(new Certificate
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Number = number,
                Status = CertificateStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("certificates", Topology.IssuedKey, held.OrderId, new CertificateIssued(held.OrderId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
