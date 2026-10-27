using Mint.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Mint.Drops;

public class DropsDbContext(DbContextOptions<DropsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "drops";

    public DbSet<Drop> Drops => Set<Drop>();
    public DbSet<UnitHold> Holds => Set<UnitHold>();
    public DbSet<DropCollector> Collectors => Set<DropCollector>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Drop>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Drops_Remaining", "\"UnitsLeft\" >= 0 AND \"UnitsLeft\" <= \"UnitsTotal\"");
                t.HasCheckConstraint("CK_Drops_Cap", "\"MaxPerCollector\" >= 1");
            });
            e.Property(x => x.MaxPerCollector).HasDefaultValue(2);
        });

        b.Entity<DropCollector>(e =>
        {
            e.ToTable("DropCollectors", t => t.HasCheckConstraint("CK_DropCollectors_Units", "\"Units\" >= 0"));
            e.HasKey(x => new { x.DropId, x.OwnerId });
            e.HasOne<Drop>().WithMany().HasForeignKey(x => x.DropId);
        });

        b.Entity<UnitHold>(e =>
        {
            e.ToTable("UnitHolds");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Drop>().WithMany().HasForeignKey(x => x.DropId);
        });
    }
}
