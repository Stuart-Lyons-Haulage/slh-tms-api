using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningOptimiserTests
{
    [Fact]
    public void Constraint_evidence_blocks_known_hours_breach_and_marks_stale_evidence_unverified()
    {
        // Defect protected: a known legal hours breach or stale Tacho snapshot is
        // ranked as a feasible recommendation merely because a driver is available.
        var now = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var evaluator = new PlanningConstraintEvaluator();

        var blocked = evaluator.EvaluateDriver(new PlanningDriverEvidence(
            DriverId: Guid.NewGuid(),
            RequiredDriveMinutes: 240,
            DriveAvailableTodayMinutes: 180,
            TachoObservedAtUtc: now.AddMinutes(-10),
            EvidenceCapturedAtUtc: now,
            ConsecutiveDays: 4,
            AlternatingSixthDayAllowed: false));
        var stale = evaluator.EvaluateDriver(new PlanningDriverEvidence(
            DriverId: Guid.NewGuid(),
            RequiredDriveMinutes: 120,
            DriveAvailableTodayMinutes: 300,
            TachoObservedAtUtc: now.AddHours(-7),
            EvidenceCapturedAtUtc: now,
            ConsecutiveDays: 5,
            AlternatingSixthDayAllowed: true));

        Assert.Equal("Blocked", blocked.Classification);
        Assert.Contains(blocked.Results, result => result.Code == "InsufficientDriveTime" && !result.Passed);
        Assert.Equal("Unverified", stale.Classification);
        Assert.Contains(stale.Results, result => result.Code == "TachoEvidenceStale" && !result.Passed);
        Assert.Contains(stale.Results, result => result.Code == "AlternatingSixthDay" && result.Passed);
    }

    [Fact]
    public async Task Generate_persists_an_immutable_proposal_without_creating_live_runs()
    {
        // Defect protected: proposal generation accidentally writes an optimiser
        // suggestion into live Loads or loses the source-line pallet quantity.
        var date = new DateOnly(2026, 8, 24);
        var movementId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var sourceLineId = Guid.NewGuid();
        var stagedId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        db.StagedImports.Add(new StagedImport
        {
            Id = stagedId,
            EntityType = "order",
            IdempotencyKey = "optimiser-order-1",
            PayloadJson = "{}",
            Status = StagingStatus.Approved
        });
        db.OrderMovements.Add(new OrderMovement
        {
            Id = movementId,
            CustomerCode = "ALDI",
            StableMovementKey = "ALDI:PO-1001",
            CurrentRevisionId = revisionId,
            LifecycleStatus = OrderMovementStatus.PlannerReady
        });
        db.OrderRevisions.Add(new OrderRevision
        {
            Id = revisionId,
            MovementId = movementId,
            StagedImportId = stagedId,
            RevisionNumber = 1,
            PayloadJson = "{}"
        });
        db.OrderSourceLines.Add(new OrderSourceLine
        {
            Id = sourceLineId,
            RevisionId = revisionId,
            SourceRowKey = "Sheet1!2",
            CollectionSite = "Aldi Swindon",
            DeliverySite = "New Covent Garden",
            CollectionDate = date,
            DeliveryDate = date.AddDays(1),
            CollectionTimeFrom = new TimeOnly(3, 0),
            PalletType = "Standard",
            Pallets = 18,
            TemperatureRequirement = "Chilled",
            LoadReference = "PO-1001",
            PayloadJson = "{}"
        });
        await db.SaveChangesAsync();

        var service = new PlanningOptimiserService(db, NullLogger<PlanningOptimiserService>.Instance);
        var proposal = await service.GenerateAsync(
            new GeneratePlanProposalRequest(date, "AM"),
            "planner@lyonshaulage.com",
            CancellationToken.None);

        Assert.Equal(date, proposal.PlanningDate);
        Assert.Equal("Unverified", proposal.Classification);
        var run = Assert.Single(proposal.Runs);
        var allocation = Assert.Single(run.Allocations);
        Assert.Equal(sourceLineId, allocation.SourceLineId);
        Assert.Equal(18, allocation.Pallets);
        Assert.Contains(proposal.Warnings, warning => warning.Code == "TachoEvidenceMissing");
        Assert.Empty(await db.Loads.ToListAsync());
        Assert.Empty(await db.LoadStops.ToListAsync());

        var stored = Assert.Single(await db.PlanProposals.AsNoTracking().ToListAsync());
        Assert.Equal(proposal.Id, stored.Id);
        Assert.Equal("Generated", stored.Status);
    }
}
