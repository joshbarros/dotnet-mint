using Mint.Shared;
using Mint.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mint.Certificates;

public static class CertificatesModule
{
    public static IServiceCollection AddCertificatesModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Mint") ?? throw new InvalidOperationException("Connection string 'Mint' is not configured.");
        services.AddDbContext<CertificatesDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", CertificatesDbContext.SchemaName)));
        services.AddScoped<CertificatesHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<CertificatesDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<CertificatesDbContext>>("certificates-db", tags: ["ready"]);

        messaging.AddOutbox<CertificatesDbContext>()
            .AddConsumer<CertificatesHandler>(Topology.CertificatesQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
