using Microsoft.EntityFrameworkCore;

using P4ServerManager.Daemon.Database.Entities;

namespace P4ServerManager.Daemon.Database;

public sealed class DaemonDbContext(DbContextOptions<DaemonDbContext> options) : DbContext(options)
{
    public DbSet<P4ServerInstance> Instances => Set<P4ServerInstance>();

    public DbSet<P4ServerBuild> Builds => Set<P4ServerBuild>();

    public DbSet<BuildArchivePolicy> BuildArchivePolicies => Set<BuildArchivePolicy>();

    public DbSet<HealthCheckRun> HealthCheckRuns => Set<HealthCheckRun>();

    public DbSet<HealthCheckFinding> HealthCheckFindings => Set<HealthCheckFinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<P4ServerInstance>(instance =>
        {
            instance.Property(i => i.State).HasConversion<string>().HasMaxLength(16);
            instance.HasOne(i => i.Build)
                .WithMany(b => b.Instances)
                .HasForeignKey(i => i.BuildId)
                .OnDelete(DeleteBehavior.Restrict);
            instance.OwnsOne(i => i.SuperUser, superUser =>
            {
                superUser.ToTable("InstanceSuperUsers");
                superUser.WithOwner().HasForeignKey("InstanceId");
                superUser.Property(u => u.Status).HasConversion<string>().HasMaxLength(32);
            });
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

        modelBuilder.Entity<HealthCheckRun>(run =>
        {
            run.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            run.PrimitiveCollection(r => r.Checks).ElementType().HasConversion<string>();
            run.HasIndex(r => r.InstanceId);
            run.HasOne<P4ServerInstance>()
                .WithMany()
                .HasForeignKey(r => r.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            run.HasMany(r => r.Findings)
                .WithOne()
                .HasForeignKey(f => f.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HealthCheckFinding>(finding =>
        {
            finding.Property(f => f.Check).HasConversion<string>().HasMaxLength(16);
        });
    }
}
