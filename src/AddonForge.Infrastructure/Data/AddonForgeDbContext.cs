using System.Text.Json;
using AddonForge.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AddonForge.Infrastructure.Data;

public class AddonForgeDbContext(DbContextOptions<AddonForgeDbContext> options) : DbContext(options)
{
    public DbSet<ManagedAddon> ManagedAddons => Set<ManagedAddon>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagedAddon>(entity =>
        {
            entity.HasKey(a => a.Slug);
            entity.Property(a => a.Slug).HasMaxLength(128);
            entity.Property(a => a.Name).HasMaxLength(256).IsRequired();
            entity.Property(a => a.SourceRepoUrl).IsRequired();
            entity.Ignore(a => a.Status);
            entity.Ignore(a => a.DataAtRisk);

            entity.Property(a => a.PersistentPaths)
                .HasConversion(
                    v => string.Join('\n', v),
                    v => v.Length == 0 ? new List<string>() : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
                    v => v.ToList()));

            entity.Property(a => a.SharedPaths)
                .HasConversion(
                    v => string.Join('\n', v),
                    v => v.Length == 0 ? new List<string>() : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
                    v => v.ToList()));

            entity.Property(a => a.DetectedPersistentPaths)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrEmpty(v)
                        ? new List<PersistentPathCandidate>()
                        : JsonSerializer.Deserialize<List<PersistentPathCandidate>>(v, (JsonSerializerOptions?)null)
                          ?? new List<PersistentPathCandidate>())
                .Metadata.SetValueComparer(new ValueComparer<List<PersistentPathCandidate>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, c) => HashCode.Combine(hash, c.GetHashCode())),
                    v => v.ToList()));

            // HA map targets (e.g. "media:rw") never contain commas, so a
            // simple joined string is safe here - no need for JSON.
            entity.Property(a => a.MapEntries)
                .HasConversion(
                    v => string.Join(',', v),
                    v => v.Length == 0 ? new List<string>() : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
                    v => v.ToList()));

            // Unlike map entries, a "KEY=value" line's value can legitimately
            // contain a comma (e.g. a comma-separated default list), so this
            // is joined on newlines instead - each entry is one line anyway.
            entity.Property(a => a.ManualEnvVars)
                .HasConversion(
                    v => string.Join('\n', v),
                    v => v.Length == 0 ? new List<string>() : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
                    v => v.ToList()));

            // A manual port's optional description can contain anything
            // (including a comma), so this is joined on newlines like
            // ManualEnvVars, not comma-joined like MapEntries.
            entity.Property(a => a.ManualPorts)
                .HasConversion(
                    v => string.Join('\n', v),
                    v => v.Length == 0 ? new List<string>() : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
                    v => v.ToList()));
        });
    }
}
