using Mint.Shared;
using Mint.Shared.Auth;
using Mint.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mint.Drops;

public record CreateDropRequest(string? Code, string? Name, string? Series, string? Artist, DateTimeOffset? ReleaseDate, long UnitsTotal, long PriceCents, long? MaxPerCollector = null);

public record DropDto(Guid Id, string Code, string Name, string Series, string Artist, DateTimeOffset ReleaseDate, long PriceCents, int UnitsTotal, int UnitsLeft, int MaxPerCollector)
{
    public static DropDto From(Drop s) => new(s.Id, s.Code, s.Name, s.Series, s.Artist, s.ReleaseDate, s.PriceCents, s.UnitsTotal, s.UnitsLeft, s.MaxPerCollector);
}

/// <summary>What other modules may ask Drops, over Drops's own tables.</summary>
public sealed class DropCatalog(DropsDbContext db) : IDropCatalog
{
    public async Task<DropInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Drops.AsNoTracking().Where(s => s.Code == code).Select(s => new DropInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/drops")]
[Authorize]
public class DropsController(DropsDbContext db) : ControllerBase
{
    private const int MaxUnitsTotal = 5000;
    private const long MaxPriceCents = 1_000_000;

    [HttpGet]
    public async Task<IEnumerable<DropDto>> List(CancellationToken ct) =>
        (await db.Drops.AsNoTracking().OrderBy(s => s.ReleaseDate).ThenBy(s => s.Code).ToListAsync(ct)).Select(DropDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<DropDto>> Get(string code, CancellationToken ct)
    {
        var drop = await db.Drops.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return drop is null ? NotFound() : DropDto.From(drop);
    }

    /// <summary>Opens a drop. Staff only. This is the only place UnitsLeft is initialised; after that only the Drops handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<DropDto>> Create(CreateDropRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Series) || string.IsNullOrWhiteSpace(request.Artist))
            throw new DomainException(400, "Name, series and artist are required.");
        if (request.UnitsTotal < 1 || request.UnitsTotal > MaxUnitsTotal)
            throw new DomainException(400, $"Units on sale must be between 1 and {MaxUnitsTotal} units.");
        if (request.PriceCents < 1 || request.PriceCents > MaxPriceCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxPriceCents} cents per unit.");
        var cap = request.MaxPerCollector ?? 2;
        if (cap < 1 || cap > request.UnitsTotal)
            throw new DomainException(400, "Per-collector cap must be at least 1 and no more than the units on sale.");
        if (await db.Drops.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Drop '{code}' already exists.");

        var drop = new Drop
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Series = request.Series.Trim(),
            Artist = request.Artist.Trim(),
            ReleaseDate = request.ReleaseDate ?? DateTimeOffset.UtcNow.AddDays(14),
            PriceCents = request.PriceCents,
            UnitsTotal = (int)request.UnitsTotal,
            UnitsLeft = (int)request.UnitsTotal,
            MaxPerCollector = (int)cap,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Drops.Add(drop);
        await db.SaveChangesAsync(ct);
        return Created($"/api/drops/{drop.Code}", DropDto.From(drop));
    }
}
