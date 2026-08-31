using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

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
    public DbSet<StagedImportEvent> StagedImportEvents => Set<StagedImportEvent>();
    public DbSet<OrderMovement> OrderMovements => Set<OrderMovement>();
    public DbSet<OrderRevision> OrderRevisions => Set<OrderRevision>();
    public DbSet<OrderSourceLine> OrderSourceLines => Set<OrderSourceLine>();
    public DbSet<PlanProposal> PlanProposals => Set<PlanProposal>();
    public DbSet<PlanProposalRun> PlanProposalRuns => Set<PlanProposalRun>();
    public DbSet<PlanProposalAllocation> PlanProposalAllocations => Set<PlanProposalAllocation>();
    public DbSet<PlanProposalCandidate> PlanProposalCandidates => Set<PlanProposalCandidate>();
    public DbSet<OrderReferenceIssue> OrderReferenceIssues => Set<OrderReferenceIssue>();
    public DbSet<ReferenceChaseEvent> ReferenceChaseEvents => Set<ReferenceChaseEvent>();
    public DbSet<TransportOrder> TransportOrders => Set<TransportOrder>();
    public DbSet<Load> Loads => Set<Load>();
    public DbSet<LoadStop> LoadStops => Set<LoadStop>();
    public DbSet<VehicleTrackingEvent> VehicleTrackingEvents => Set<VehicleTrackingEvent>();
    public DbSet<VehicleLiveStatus> VehicleLiveStatuses => Set<VehicleLiveStatus>();
    public DbSet<FuelPrice> FuelPrices => Set<FuelPrice>();
    public DbSet<IntegrationMapping> IntegrationMappings => Set<IntegrationMapping>();
    public DbSet<DriverStatusLog> DriverStatusLogs => Set<DriverStatusLog>();
    public DbSet<MasterDataAudit> MasterDataAudits => Set<MasterDataAudit>();
    public DbSet<AuditOutbox> AuditOutboxes => Set<AuditOutbox>();
    public DbSet<SiteGeofence> SiteGeofences => Set<SiteGeofence>();
    public DbSet<GeofenceVisit> GeofenceVisits => Set<GeofenceVisit>();
    public DbSet<EtaSnapshot> EtaSnapshots => Set<EtaSnapshot>();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var completionTransitions = ChangeTracker.Entries<Load>()
            .Where(entry =>
                entry.Entity.Status == LoadStatus.Completed
                && (entry.State == EntityState.Added
                    || entry.State == EntityState.Modified
                    && entry.Property(load => load.Status).IsModified
                    && entry.Property(load => load.Status).OriginalValue != LoadStatus.Completed))
            .Select(entry => entry.Entity.Id)
            .Distinct()
            .ToList();

        foreach (var loadId in completionTransitions)
            await RunCompletionPersistenceGuard.EnsureCompletionEvidenceAsync(this, loadId, cancellationToken);

        EnqueuePendingMasterDataAudits();
        return await base.SaveChangesAsync(cancellationToken);
    }

    internal Task<int> SaveAuditReplayChangesAsync(CancellationToken cancellationToken = default) =>
        base.SaveChangesAsync(cancellationToken);

    private void EnqueuePendingMasterDataAudits()
    {
        var pendingAudits = ChangeTracker.Entries<MasterDataAudit>()
            .Where(entry => entry.State == EntityState.Added)
            .ToList();

        foreach (var entry in pendingAudits)
        {
            var audit = entry.Entity;
            AuditOutboxes.Add(new AuditOutbox
            {
                EventType = AuditOutboxEventTypes.MasterDataAudit,
                Payload = JsonSerializer.Serialize(audit),
                CreatedAt = DateTimeOffset.UtcNow,
                RetryCount = 0
            });

            entry.State = EntityState.Detached;
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        b.Entity<CustomerContact>().HasIndex(x => new { x.CustomerCode, x.Name }).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.Registration).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.EmployeeNumber).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.TachoMasterDriverId)
            .HasDatabaseName("IX_Drivers_TachoMasterDriverId")
            .HasFilter("[TachoMasterDriverId] IS NOT NULL");
        b.Entity<Trailer>().HasIndex(x => x.TrailerNumber).IsUnique();
        b.Entity<Site>().HasIndex(x => x.ExternalCode).IsUnique();
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer")
            b.Entity<Site>().Ignore(x => x.OperationalRegion);
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
        b.Entity<IntegrationMapping>().Property(x => x.ConfidenceThreshold).HasPrecision(5, 4);

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

        b.Entity<AuditOutbox>().ToTable("AuditOutbox");
        b.Entity<AuditOutbox>().HasKey(x => x.OutboxId);
        b.Entity<AuditOutbox>().Property(x => x.Payload).HasColumnType("nvarchar(max)");
        b.Entity<AuditOutbox>()
            .HasIndex(x => new { x.ProcessedAt, x.FailedAt, x.CreatedAt })
            .HasDatabaseName("IX_AuditOutbox_Pending");
        b.Entity<AuditOutbox>()
            .HasIndex(x => x.CreatedAt)
            .HasDatabaseName("IX_AuditOutbox_CreatedAt");

        b.Entity<StagedImport>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<StagedImport>().Property(x => x.RowVersion).IsRowVersion();
        b.Entity<StagedImportEvent>().HasIndex(x => new { x.StagedImportId, x.OccurredAtUtc });
        b.Entity<StagedImportEvent>().HasOne<StagedImport>().WithMany().HasForeignKey(x => x.StagedImportId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderMovement>().HasIndex(x => new { x.CustomerCode, x.StableMovementKey }).IsUnique();
        b.Entity<OrderRevision>().HasIndex(x => new { x.MovementId, x.RevisionNumber }).IsUnique();
        b.Entity<OrderRevision>().HasIndex(x => x.StagedImportId).IsUnique();
        b.Entity<OrderRevision>().HasOne<OrderMovement>().WithMany().HasForeignKey(x => x.MovementId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderRevision>().HasOne<StagedImport>().WithMany().HasForeignKey(x => x.StagedImportId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderSourceLine>().HasIndex(x => new { x.RevisionId, x.SourceRowKey }).IsUnique();
        b.Entity<OrderSourceLine>().HasOne<OrderRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposal>().HasIndex(x => new { x.PlanningDate, x.Period, x.Version }).IsUnique();
        b.Entity<PlanProposal>().HasIndex(x => x.InputHash);
        b.Entity<PlanProposalRun>().HasIndex(x => new { x.ProposalId, x.Sequence }).IsUnique();
        b.Entity<PlanProposalRun>().Property(x => x.Score).HasPrecision(10, 2);
        b.Entity<PlanProposalRun>().HasOne<Driver>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposalRun>().HasOne<Vehicle>().WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposalRun>().HasOne<Trailer>().WithMany().HasForeignKey(x => x.TrailerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposal>().HasMany(x => x.Runs).WithOne().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanProposalAllocation>().HasIndex(x => new { x.ProposalRunId, x.SourceLineId });
        b.Entity<PlanProposalRun>().HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.ProposalRunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanProposalAllocation>().HasOne<OrderSourceLine>().WithMany().HasForeignKey(x => x.SourceLineId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposalCandidate>().HasIndex(x => new { x.ProposalRunId, x.DriverId, x.VehicleId }).IsUnique();
        b.Entity<PlanProposalCandidate>().Property(x => x.Score).HasPrecision(10, 2);
        b.Entity<PlanProposalRun>().HasMany(x => x.Candidates).WithOne().HasForeignKey(x => x.ProposalRunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanProposalCandidate>().HasOne<Driver>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposalCandidate>().HasOne<Vehicle>().WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderReferenceIssue>().HasIndex(x => new { x.MovementId, x.ReferenceType, x.Status });
        b.Entity<OrderReferenceIssue>().HasOne<OrderMovement>().WithMany().HasForeignKey(x => x.MovementId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ReferenceChaseEvent>().HasIndex(x => new { x.ReferenceIssueId, x.OccurredAtUtc });
        b.Entity<ReferenceChaseEvent>().HasOne<OrderReferenceIssue>().WithMany().HasForeignKey(x => x.ReferenceIssueId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TransportOrder>().HasIndex(x => x.Reference).IsUnique();
        b.Entity<TransportOrder>().HasIndex(x => x.CollectionDate);
        b.Entity<TransportOrder>().HasIndex(x => x.SourceStagedImportId);
        b.Entity<TransportOrder>().HasOne<StagedImport>().WithMany().HasForeignKey(x => x.SourceStagedImportId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TransportOrder>().HasIndex(x => x.SourceMovementId);
        b.Entity<TransportOrder>().HasOne<OrderMovement>().WithMany().HasForeignKey(x => x.SourceMovementId).OnDelete(DeleteBehavior.Restrict);
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
