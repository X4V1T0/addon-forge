using AddonForge.Core.Models;

namespace AddonForge.Core.Abstractions;

public interface IManagedAddonRepository
{
    Task<IReadOnlyList<ManagedAddon>> GetAllAsync(CancellationToken ct);

    Task<ManagedAddon?> GetBySlugAsync(string slug, CancellationToken ct);

    Task AddAsync(ManagedAddon addon, CancellationToken ct);

    Task UpdateAsync(ManagedAddon addon, CancellationToken ct);

    Task DeleteAsync(string slug, CancellationToken ct);
}
