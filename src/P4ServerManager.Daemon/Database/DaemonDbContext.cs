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

    public DbSet<BackupDestination> BackupDestinations => Set<BackupDestination>();

    public DbSet<InstanceBackupDestination> InstanceBackupDestinations => Set<InstanceBackupDestination>();

    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();

    public DbSet<BackupRunDestination> BackupRunDestinations => Set<BackupRunDestination>();

    public DbSet<RestoreRun> RestoreRuns => Set<RestoreRun>();

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Only what data annotations cannot express lives here: enums stored as text, the owned super user and the
        // case-insensitive user names.
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

        modelBuilder.Entity<BackupDestination>()
            .Property(d => d.Kind).HasConversion<string>();

        modelBuilder.Entity<BackupRun>(run =>
        {
            run.Property(r => r.Kind).HasConversion<string>();
            run.Property(r => r.Trigger).HasConversion<string>();
            run.Property(r => r.Status).HasConversion<string>();
        });

        modelBuilder.Entity<BackupRunDestination>()
            .Property(d => d.Status).HasConversion<string>();

        modelBuilder.Entity<RestoreRun>(run =>
        {
            run.Property(r => r.Target).HasConversion<string>();
            run.Property(r => r.Status).HasConversion<string>();
        });

        modelBuilder.Entity<User>()
            .Property(u => u.UserName).UseCollation("NOCASE");
    }
}
