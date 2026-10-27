using Mint.Shared;
using Mint.Shared.Contracts;
using Mint.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mint.Drops;

public static class DropsModule
{
    public static IServiceCollection AddDropsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Mint") ?? throw new InvalidOperationException("Connection string 'Mint' is not configured.");
        services.AddDbContext<DropsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", DropsDbContext.SchemaName)));
        services.AddScoped<DropsHandler>();
        services.AddScoped<IDropCatalog, DropCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<DropsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<DropsDbContext>>("units-db", tags: ["ready"]);

        messaging.AddOutbox<DropsDbContext>()
            .AddConsumer<DropsHandler>(Topology.DropsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
