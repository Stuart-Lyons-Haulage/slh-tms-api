using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Data;
public sealed class TmsDbContext(DbContextOptions<TmsDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<StagedImport> StagedImports => Set<StagedImport>();
    public DbSet<VehicleTrackingEvent> VehicleTrackingEvents => Set<VehicleTrackingEvent>();
    public DbSet<VehicleLiveStatus> VehicleLiveStatuses => Set<VehicleLiveStatus>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.Registration).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.EmployeeNumber).IsUnique();
        b.Entity<StagedImport>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<StagedImport>().Property(x => x.RowVersion).IsRowVersion();

        // Tracking entities configuration
        b.Entity<VehicleTrackingEvent>()
            .HasIndex(x => new { x.ProviderName, x.ProviderEventId })
            .IsUnique()
            .HasDatabaseName("IX_VehicleTrackingEvent_ProviderName_ProviderEventId");

        b.Entity<VehicleTrackingEvent>()
            .HasIndex(x => x.VehicleIdentifier)
            .HasDatabaseName("IX_VehicleTrackingEvent_VehicleIdentifier");

        b.Entity<VehicleTrackingEvent>()
            .HasIndex(x => x.EventTimeUtc)
            .HasDatabaseName("IX_VehicleTrackingEvent_EventTimeUtc");

        b.Entity<VehicleLiveStatus>()
            .HasIndex(x => x.VehicleIdentifier)
            .IsUnique()
            .HasDatabaseName("IX_VehicleLiveStatus_VehicleIdentifier");

        b.Entity<VehicleLiveStatus>()
            .HasIndex(x => x.LastEventTimeUtc)
            .HasDatabaseName("IX_VehicleLiveStatus_LastEventTimeUtc");
    }
}
