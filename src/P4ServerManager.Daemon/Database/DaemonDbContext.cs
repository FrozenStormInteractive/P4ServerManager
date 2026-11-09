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
        // Only what data annotations cannot express lives here: enums stored as text and the owned super user.
        modelBuilder.Entity<P4ServerInstance>(instance =>
        {
            instance.Property(i => i.State).HasConversion<string>();
            instance.OwnsOne(i => i.SuperUser, superUser =>
            {
                superUser.ToTable("InstanceSuperUsers");
                superUser.WithOwner().HasForeignKey("InstanceId");
                superUser.Property(u => u.Status).HasConversion<string>();
            });
        });

        modelBuilder.Entity<BuildArchivePolicy>()
            .Property(p => p.Scope).HasConversion<string>();

        modelBuilder.Entity<HealthCheckRun>(run =>
        {
            run.Property(r => r.Status).HasConversion<string>();
            run.PrimitiveCollection(r => r.Checks).ElementType().HasConversion<string>();
        });

        modelBuilder.Entity<HealthCheckFinding>()
            .Property(f => f.Check).HasConversion<string>();
    }
}
