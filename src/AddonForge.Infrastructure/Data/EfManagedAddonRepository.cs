using AddonForge.Core.Abstractions;
using AddonForge.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AddonForge.Infrastructure.Data;

public class EfManagedAddonRepository(AddonForgeDbContext db) : IManagedAddonRepository
{
    public async Task<IReadOnlyList<ManagedAddon>> GetAllAsync(CancellationToken ct) =>
        await db.ManagedAddons.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);

    public async Task<ManagedAddon?> GetBySlugAsync(string slug, CancellationToken ct) =>
        await db.ManagedAddons.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == slug, ct);

    public async Task AddAsync(ManagedAddon addon, CancellationToken ct)
    {
        db.ManagedAddons.Add(addon);
        await db.SaveChangesAsync(ct);
        db.Entry(addon).State = EntityState.Detached;
    }

    public async Task UpdateAsync(ManagedAddon addon, CancellationToken ct)
    {
        db.ManagedAddons.Update(addon);
        await db.SaveChangesAsync(ct);

        // Detach once saved - the ASP.NET Core DbContext is scoped per
        // request, and more than one call in here can touch the same addon
        // within a single request (e.g. a page handler saves a field, then
        // AddonUpdateService.RegenerateAsync re-fetches and saves it again
        // itself). Leaving this instance attached made that second Update()
        // throw: "another instance with the same key value ... is already
        // being tracked" - a real bug hit via the Config file / extra env
        // vars "Save & regenerate" actions. GetBySlugAsync's own
        // AsNoTracking() reads are unaffected by this either way.
        db.Entry(addon).State = EntityState.Detached;
    }

    public async Task DeleteAsync(string slug, CancellationToken ct)
    {
        var existing = await db.ManagedAddons.FirstOrDefaultAsync(a => a.Slug == slug, ct);
        if (existing is not null)
        {
            db.ManagedAddons.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
    }
}
