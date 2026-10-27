namespace Mint.Shared.Contracts;

public record DropInfo(Guid Id, string Code, long PriceCents);

/// <summary>
/// What Orders may ask Drops, without referencing it. Drops implements it over its own tables;
/// Orders never reads another module's schema.
/// </summary>
public interface IDropCatalog
{
    Task<DropInfo?> FindAsync(string code, CancellationToken ct);
}
