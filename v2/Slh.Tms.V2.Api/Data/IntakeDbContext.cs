using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Domain;

namespace Slh.Tms.V2.Api.Data;

public sealed class IntakeRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid EvidenceId { get; set; }
    public required string ExtractedJson { get; set; }
    public string? ResolutionJson { get; set; }
    public IntakeReviewState State { get; set; } = IntakeReviewState.Received;
    public decimal Confidence { get; set; }
    public string? ReviewReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IntakeDbContext(DbContextOptions<IntakeDbContext> options) : DbContext(options)
{
    public DbSet<SourceEvidence> Evidence => Set<SourceEvidence>();
    public DbSet<IntakeRecord> IntakeRecords => Set<IntakeRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("intake");

        b.Entity<SourceEvidence>().HasKey(x => x.Id);
        b.Entity<SourceEvidence>().HasIndex(x => x.EvidenceHash).IsUnique();
        b.Entity<SourceEvidence>().HasIndex(x => new { x.SourceSystem, x.MessageId });
        b.Entity<SourceEvidence>().Property(x => x.SourceSystem).HasMaxLength(80);
        b.Entity<SourceEvidence>().Property(x => x.EvidenceHash).HasMaxLength(128);

        b.Entity<IntakeRecord>().HasKey(x => x.Id);
        b.Entity<IntakeRecord>().HasIndex(x => x.EvidenceId).IsUnique();
        b.Entity<IntakeRecord>().HasIndex(x => new { x.State, x.CreatedAtUtc });
        b.Entity<IntakeRecord>().Property(x => x.Confidence).HasPrecision(5, 4);
    }
}
