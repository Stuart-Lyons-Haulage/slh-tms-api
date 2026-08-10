using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Data;
public sealed class TmsDbContext(DbContextOptions<TmsDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<Trailer> Trailers => Set<Trailer>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<MarketContact> MarketContacts => Set<MarketContact>();
    public DbSet<StagedImport> StagedImports => Set<StagedImport>();
    public DbSet<TransportOrder> TransportOrders => Set<TransportOrder>();
    public DbSet<Load> Loads => Set<Load>();
    public DbSet<LoadStop> LoadStops => Set<LoadStop>();
    public DbSet<VehicleTrackingEvent> VehicleTrackingEvents => Set<VehicleTrackingEvent>();
    public DbSet<VehicleLiveStatus> VehicleLiveStatuses => Set<VehicleLiveStatus>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.Registration).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.EmployeeNumber).IsUnique();
        b.Entity<Trailer>().HasIndex(x => x.TrailerNumber).IsUnique();
        b.Entity<Site>().HasIndex(x => x.ExternalCode).IsUnique();
        b.Entity<MarketContact>().HasIndex(x => new { x.Market, x.Name }).IsUnique();
        b.Entity<StagedImport>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<StagedImport>().Property(x => x.RowVersion).IsRowVersion();
        b.Entity<TransportOrder>().HasIndex(x => x.Reference).IsUnique();
        b.Entity<TransportOrder>().HasIndex(x => x.CollectionDate);
        b.Entity<Load>().HasIndex(x => x.Reference).IsUnique();
        b.Entity<Load>().HasIndex(x => x.PlanningDate);
        b.Entity<LoadStop>().HasIndex(x => new { x.LoadId, x.Sequence }).IsUnique();
        b.Entity<Load>().HasMany(x => x.Stops).WithOne().HasForeignKey(x => x.LoadId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LoadStop>().Property(x => x.Latitude).HasPrecision(9, 6);
        b.Entity<LoadStop>().Property(x => x.Longitude).HasPrecision(9, 6);

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

        b.Entity<VehicleTrackingEvent>().Property(x => x.Latitude).HasPrecision(9, 6);
        b.Entity<VehicleTrackingEvent>().Property(x => x.Longitude).HasPrecision(9, 6);
        b.Entity<VehicleTrackingEvent>().Property(x => x.SpeedKph).HasPrecision(10, 2);

        b.Entity<VehicleLiveStatus>()
            .HasIndex(x => x.VehicleIdentifier)
            .IsUnique()
            .HasDatabaseName("IX_VehicleLiveStatus_VehicleIdentifier");

        b.Entity<VehicleLiveStatus>()
            .HasIndex(x => x.LastEventTimeUtc)
            .HasDatabaseName("IX_VehicleLiveStatus_LastEventTimeUtc");

        b.Entity<VehicleLiveStatus>().Property(x => x.Latitude).HasPrecision(9, 6);
        b.Entity<VehicleLiveStatus>().Property(x => x.Longitude).HasPrecision(9, 6);
        b.Entity<VehicleLiveStatus>().Property(x => x.SpeedKph).HasPrecision(10, 2);
    }
}
