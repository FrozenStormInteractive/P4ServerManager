using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Database;

public sealed class DaemonDbContext(DbContextOptions<DaemonDbContext> options) : DbContext(options)
{
    public DbSet<P4ServerInstance> Instances => Set<P4ServerInstance>();

    public DbSet<P4ServerBuild> Builds => Set<P4ServerBuild>();

    public DbSet<BuildArchivePolicy> BuildArchivePolicies => Set<BuildArchivePolicy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<P4ServerInstance>(instance =>
        {
            instance.Property(i => i.State).HasConversion<string>().HasMaxLength(16);
            instance.HasOne(i => i.Build)
                .WithMany(b => b.Instances)
                .HasForeignKey(i => i.BuildId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<P4ServerBuild>(build =>
        {
            build.Property(b => b.Release).HasMaxLength(16);
            build.Property(b => b.Version).HasMaxLength(16);
            build.Property(b => b.BuildNumber).HasMaxLength(16);
            build.Property(b => b.Platform).HasMaxLength(16);
            build.Property(b => b.Sha256).HasMaxLength(64);
            build.Property(b => b.P4Sha256).HasMaxLength(64);
            build.HasIndex(b => new { b.Platform, b.Version, b.BuildNumber }).IsUnique();
        });

        modelBuilder.Entity<BuildArchivePolicy>(policy =>
        {
            policy.Property(p => p.Id).ValueGeneratedNever();
            policy.Property(p => p.Scope).HasConversion<string>().HasMaxLength(16);
        });
    }
}
