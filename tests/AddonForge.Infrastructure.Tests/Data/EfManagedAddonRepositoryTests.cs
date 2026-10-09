using AddonForge.Core.Models;
using AddonForge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonForge.Infrastructure.Tests.Data;

/// <summary>
/// Uses the real SQLite provider (not a hand-written fake) since this is
/// specifically about EF Core's change tracker, which a fake repository
/// wouldn't exercise at all.
/// </summary>
public class EfManagedAddonRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "addon-forge-tests", $"{Guid.NewGuid():N}.db");
    private readonly AddonForgeDbContext _db;
    private readonly EfManagedAddonRepository _repository;

    public EfManagedAddonRepositoryTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        // Pooling=False so the SQLite file handle is actually released once
        // the context is disposed, instead of lingering in the connection
        // pool and blocking the cleanup delete in Dispose() below.
        var options = new DbContextOptionsBuilder<AddonForgeDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        _db = new AddonForgeDbContext(options);
        _db.Database.Migrate();
        _repository = new EfManagedAddonRepository(_db);
    }

    // Regression coverage for a real bug: ASP.NET Core scopes one DbContext
    // per request, and a "Save & regenerate" action does exactly this
    // sequence within a single request - the page handler fetches, mutates
    // and saves one field, then AddonUpdateService.RegenerateAsync
    // independently fetches, mutates and saves the addon again itself.
    // UpdateAsync left its entity attached after SaveChanges, so the second
    // call (a different in-memory instance, same key) failed with "The
    // instance of entity type 'ManagedAddon' cannot be tracked because
    // another instance with the same key value for {'Slug'} is already
    // being tracked."
    [Fact]
    public async Task UpdateAsync_CalledTwiceForSameSlugInSameContext_DoesNotThrow()
    {
        await _repository.AddAsync(new ManagedAddon
        {
            Slug = "my_addon",
            Name = "My Addon",
            SourceRepoUrl = "https://example.com/repo",
            Description = "desc",
        }, CancellationToken.None);

        var first = await _repository.GetBySlugAsync("my_addon", CancellationToken.None);
        first!.ConfigFileTemplate = "weather:\n  location: ${LOC}\n";
        await _repository.UpdateAsync(first, CancellationToken.None);

        var second = await _repository.GetBySlugAsync("my_addon", CancellationToken.None);
        second!.LastWrittenAt = DateTimeOffset.UtcNow;
        second.LastError = null;

        var ex = await Record.ExceptionAsync(() => _repository.UpdateAsync(second, CancellationToken.None));

        Assert.Null(ex);
        var final = await _repository.GetBySlugAsync("my_addon", CancellationToken.None);
        Assert.Equal("weather:\n  location: ${LOC}\n", final!.ConfigFileTemplate);
        Assert.NotNull(final.LastWrittenAt);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
