using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Data;
public sealed class TmsDbContext(DbContextOptions<TmsDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerContact> CustomerContacts => Set<CustomerContact>();
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
    public DbSet<FuelPrice> FuelPrices => Set<FuelPrice>();
    public DbSet<IntegrationMapping> IntegrationMappings => Set<IntegrationMapping>();
    public DbSet<DriverStatusLog> DriverStatusLogs => Set<DriverStatusLog>();
    public DbSet<MasterDataAudit> MasterDataAudits => Set<MasterDataAudit>();
    public DbSet<SiteGeofence> SiteGeofences => Set<SiteGeofence>();
    public DbSet<GeofenceVisit> GeofenceVisits => Set<GeofenceVisit>();
    public DbSet<EtaSnapshot> EtaSnapshots => Set<EtaSnapshot>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        b.Entity<CustomerContact>().HasIndex(x => new { x.CustomerCode, x.Name }).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.Registration).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.EmployeeNumber).IsUnique();
        b.Entity<Trailer>().HasIndex(x => x.TrailerNumber).IsUnique();
        b.Entity<Site>().HasIndex(x => x.ExternalCode).IsUnique();
        b.Entity<MarketContact>().HasIndex(x => new { x.Market, x.Name }).IsUnique();
        b.Entity<FuelPrice>().HasIndex(x => new { x.WeekCommencing, x.Provider }).IsUnique();
        b.Entity<FuelPrice>().Property(x => x.PricePencePerLitre).HasPrecision(10, 2);

        b.Entity<IntegrationMapping>()
            .HasIndex(x => new { x.Provider, x.ExternalKey, x.TmsEntityType })
            .IsUnique()
            .HasFilter("[Active] = 1")
            .HasDatabaseName("IX_IntegrationMappings_Provider_ExternalKey_Type");
        b.Entity<IntegrationMapping>()
            .HasIndex(x => x.TmsEntityId)
            .HasDatabaseName("IX_IntegrationMappings_TmsEntityId");

        b.Entity<DriverStatusLog>()
            .HasIndex(x => x.LoadId)
            .HasDatabaseName("IX_DriverStatusLogs_LoadId");
        b.Entity<DriverStatusLog>()
            .HasIndex(x => x.CapturedAtUtc)
            .HasDatabaseName("IX_DriverStatusLogs_CapturedAtUtc");

        b.Entity<MasterDataAudit>()
            .HasIndex(x => new { x.EntityType, x.EntityId, x.ChangedAtUtc })
            .HasDatabaseName("IX_MasterDataAudits_Entity_History");
        b.Entity<MasterDataAudit>()
            .HasIndex(x => x.ChangedAtUtc)
            .HasDatabaseName("IX_MasterDataAudits_ChangedAtUtc");

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

        b.Entity<SiteGeofence>().HasIndex(x => x.NormalizedName).IsUnique();
        b.Entity<SiteGeofence>().HasIndex(x => x.SiteId);
        b.Entity<GeofenceVisit>().HasIndex(x => new { x.VehicleIdentifier, x.ExitedAtUtc });
        b.Entity<GeofenceVisit>().HasIndex(x => new { x.LoadId, x.LoadStopId });
        b.Entity<GeofenceVisit>().HasIndex(x => x.EnteredAtUtc);
        b.Entity<EtaSnapshot>().HasIndex(x => new { x.StopId, x.CapturedAtUtc });
        b.Entity<EtaSnapshot>().HasIndex(x => x.LoadId);

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