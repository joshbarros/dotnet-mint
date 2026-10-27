using Mint.Orders;
using Mint.Drops;
using Mint.Certificates;
using Mint.Identity;
using Mint.Shared;
using Mint.Shared.Auth;
using Mint.Shared.Http;
using Mint.Shared.Messaging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

if (config.GetValue("Logging:Json", false))
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
    });

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true)
    .AddApplicationPart(typeof(IdentityModule).Assembly)
    .AddApplicationPart(typeof(OrdersModule).Assembly)
    .AddApplicationPart(typeof(DropsModule).Assembly)
    .AddApplicationPart(typeof(CertificatesModule).Assembly);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddMintAuthorization();

builder.Services.AddIdentityModule(config);
var messaging = builder.Services.AddMessaging(config);
builder.Services.AddOrdersModule(config, messaging);
builder.Services.AddDropsModule(config, messaging);
builder.Services.AddCertificatesModule(config, messaging);
messaging.Start();

if (config.GetValue("ForwardedHeaders:Enabled", false))
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        // Behind the nginx container the real client address arrives in X-Forwarded-For. Trusting every proxy is only safe
        // when the API is reachable through the proxy alone; restrict KnownNetworks to the proxy's network in production.
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

var app = builder.Build();

if (config.GetValue("ForwardedHeaders:Enabled", false))
    app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ExceptionMiddleware>();
app.UseStatusCodePages();
app.UseAuthRateLimit();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/api/health", () => Results.Text("Healthy")).AllowAnonymous();
app.MapHealthChecks("/api/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") }).AllowAnonymous();
if (config.GetValue("Docs:Public", false))
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

foreach (var migrator in app.Services.GetServices<IModuleMigrator>().OrderBy(m => m.Order))
    await migrator.MigrateAsync(app.Services, CancellationToken.None);

app.Run();

public partial class Program;
