using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slh.Tms.Api.Migrations;

/// <inheritdoc />
public partial class RouteOptimisationV2 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ── ScoringConfiguration ─────────────────────────────────────────────
        migrationBuilder.CreateTable(
            name: "ScoringConfigurations",
            columns: table => new
            {
                ConfigId       = table.Column<Guid>(nullable: false),
                Version        = table.Column<string>(maxLength: 40, nullable: false),
                Description    = table.Column<string>(maxLength: 200, nullable: false),
                IsActive       = table.Column<bool>(nullable: false, defaultValue: false),
                IsAdminApproved= table.Column<bool>(nullable: false, defaultValue: false),
                ApprovedBy     = table.Column<string>(maxLength: 200, nullable: true),
                ApprovedAtUtc  = table.Column<DateTimeOffset>(nullable: true),
                MinDecisionsForLearning     = table.Column<int>(nullable: false, defaultValue: 10),
                RecencyWeightHalfLifeDays   = table.Column<double>(nullable: false, defaultValue: 30.0),
                WeightsJson    = table.Column<string>(nullable: false),
                CreatedAtUtc   = table.Column<DateTimeOffset>(nullable: false),
                CreatedBy      = table.Column<string>(maxLength: 200, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ScoringConfigurations", x => x.ConfigId));

        migrationBuilder.CreateIndex(
            name: "IX_ScoringConfigurations_IsActive",
            table: "ScoringConfigurations",
            column: "IsActive");

        // Seed default config
        migrationBuilder.InsertData(
            table: "ScoringConfigurations",
            columns: ["ConfigId","Version","Description","IsActive","IsAdminApproved",
                      "MinDecisionsForLearning","RecencyWeightHalfLifeDays","WeightsJson",
                      "CreatedAtUtc","CreatedBy"],
            values: [
                Guid.Parse("00000001-0000-0000-0000-000000000001"),
                "1.0.0",
                "Initial default scoring configuration",
                true,
                true,
                10,
                30.0,
                """{"mileageSavingPerMile":0.5,"driveTimeSavingPerMinute":0.2,"backtrackPenalty":8.0,"clusterBonus":10.0,"emptyMileagePenalty":0.8,"stopReductionBonus":3.0,"deadlineBufferBonusPerMinute":0.1,"latePenalty":50.0,"workloadBalanceBonus":5.0,"trailerUtilisationBonusPerPercent":0.3,"trailerSwapPenalty":15.0,"historicalCombinationBonus":6.0,"plannerPreferenceBonus":12.0,"priorRejectionPenalty":20.0,"customerPriorityBonus":15.0,"minDecisionsForLearning":10,"recencyWeightHalfLifeDays":30.0}""",
                DateTimeOffset.Parse("2026-09-01T00:00:00+00:00"),
                "migration"
            ]);

        // ── OptimisationAnalysis ─────────────────────────────────────────────
        migrationBuilder.CreateTable(
            name: "OptimisationAnalyses",
            columns: table => new
            {
                AnalysisId             = table.Column<Guid>(nullable: false),
                PlanningDate           = table.Column<DateOnly>(nullable: false),
                Period                 = table.Column<string>(maxLength: 10, nullable: false),
                CorrelationId          = table.Column<Guid>(nullable: false),
                OptimiserVersion       = table.Column<string>(maxLength: 40, nullable: false),
                ScoringConfigVersion   = table.Column<string>(maxLength: 40, nullable: true),
                Status                 = table.Column<string>(maxLength: 40, nullable: false, defaultValue: "Pending"),
                OverallResult          = table.Column<string>(maxLength: 80, nullable: false),
                CurrentRunCount        = table.Column<int>(nullable: false),
                CurrentTotalMiles      = table.Column<decimal>(precision: 10, scale: 2, nullable: false),
                CurrentTotalDriveMinutes = table.Column<int>(nullable: false),
                ProposedRunCount       = table.Column<int>(nullable: false),
                ProposedTotalMiles     = table.Column<decimal>(precision: 10, scale: 2, nullable: false),
                ProposedTotalDriveMinutes = table.Column<int>(nullable: false),
                RunsAffected           = table.Column<int>(nullable: false),
                EvidenceJson           = table.Column<string>(nullable: false),
                PlanVersionHash        = table.Column<string>(maxLength: 128, nullable: false),
                AnalysedAtUtc          = table.Column<DateTimeOffset>(nullable: false),
                AppliedAtUtc           = table.Column<DateTimeOffset>(nullable: true),
                CreatedBy              = table.Column<string>(maxLength: 200, nullable: true),
                AppliedBy              = table.Column<string>(maxLength: 200, nullable: true),
                RejectedBy             = table.Column<string>(maxLength: 200, nullable: true),
                RejectedAtUtc          = table.Column<DateTimeOffset>(nullable: true),
                RejectionReason        = table.Column<string>(maxLength: 500, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_OptimisationAnalyses", x => x.AnalysisId));

        migrationBuilder.CreateIndex("IX_OptimisationAnalyses_PlanningDate_Period",
            "OptimisationAnalyses", ["PlanningDate", "Period"]);
        migrationBuilder.CreateIndex("IX_OptimisationAnalyses_Status",
            "OptimisationAnalyses", "Status");

        // ── OptimisationSuggestions ───────────────────────────────────────────
        migrationBuilder.CreateTable(
            name: "OptimisationSuggestions",
            columns: table => new
            {
                SuggestionId          = table.Column<Guid>(nullable: false),
                AnalysisId            = table.Column<Guid>(nullable: false),
                Kind                  = table.Column<string>(maxLength: 40, nullable: false),
                Status                = table.Column<string>(maxLength: 40, nullable: false, defaultValue: "Pending"),
                SourceRunId           = table.Column<Guid>(nullable: true),
                SourceRunReference    = table.Column<string>(maxLength: 80, nullable: true),
                TargetRunId           = table.Column<Guid>(nullable: true),
                TargetRunReference    = table.Column<string>(maxLength: 80, nullable: true),
                OrderId               = table.Column<Guid>(nullable: true),
                OrderReference        = table.Column<string>(maxLength: 80, nullable: true),
                ConstraintClass       = table.Column<string>(maxLength: 80, nullable: false),
                Title                 = table.Column<string>(maxLength: 200, nullable: false),
                Rationale             = table.Column<string>(nullable: false),
                CurrentMiles          = table.Column<decimal>(precision: 10, scale: 2, nullable: true),
                ProposedMiles         = table.Column<decimal>(precision: 10, scale: 2, nullable: true),
                CurrentDriveMinutes   = table.Column<int>(nullable: true),
                ProposedDriveMinutes  = table.Column<int>(nullable: true),
                BeforeStopSequenceJson= table.Column<string>(nullable: false),
                AfterStopSequenceJson = table.Column<string>(nullable: false),
                ConfidenceScore       = table.Column<decimal>(precision: 5, scale: 2, nullable: false),
                BenefitScore          = table.Column<decimal>(precision: 8, scale: 2, nullable: false),
                RoutingSource         = table.Column<string>(maxLength: 80, nullable: true),
                CannotApplyReason     = table.Column<string>(maxLength: 1000, nullable: true),
                ScoreComponentsJson   = table.Column<string>(nullable: false),
                ManualEditJson        = table.Column<string>(nullable: false, defaultValue: "{}"),
                DecidedAtUtc          = table.Column<DateTimeOffset>(nullable: true),
                DecidedBy             = table.Column<string>(maxLength: 200, nullable: true),
                DecisionReason        = table.Column<string>(maxLength: 500, nullable: true),
                Sequence              = table.Column<int>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OptimisationSuggestions", x => x.SuggestionId);
                table.ForeignKey("FK_OptimisationSuggestions_Analyses",
                    x => x.AnalysisId, "OptimisationAnalyses", "AnalysisId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_OptimisationSuggestions_AnalysisId",
            "OptimisationSuggestions", "AnalysisId");
        migrationBuilder.CreateIndex("IX_OptimisationSuggestions_Status",
            "OptimisationSuggestions", "Status");
        migrationBuilder.CreateIndex("IX_OptimisationSuggestions_SourceRunId",
            "OptimisationSuggestions", "SourceRunId");

        // ── OptimisationDecisionHistory ───────────────────────────────────────
        migrationBuilder.CreateTable(
            name: "OptimisationDecisionHistory",
            columns: table => new
            {
                HistoryId               = table.Column<Guid>(nullable: false),
                AnalysisId              = table.Column<Guid>(nullable: false),
                SuggestionId            = table.Column<Guid>(nullable: false),
                PlanningDate            = table.Column<DateOnly>(nullable: false),
                Period                  = table.Column<string>(maxLength: 10, nullable: false),
                Kind                    = table.Column<string>(maxLength: 40, nullable: false),
                SourceRunReference      = table.Column<string>(maxLength: 80, nullable: true),
                TargetRunReference      = table.Column<string>(maxLength: 80, nullable: true),
                CustomerCode            = table.Column<string>(maxLength: 80, nullable: true),
                CollectionSite          = table.Column<string>(maxLength: 80, nullable: true),
                DeliverySite            = table.Column<string>(maxLength: 80, nullable: true),
                RunType                 = table.Column<string>(maxLength: 80, nullable: true),
                Decision                = table.Column<string>(maxLength: 40, nullable: false),
                DecidedBy               = table.Column<string>(maxLength: 200, nullable: true),
                DecisionReason          = table.Column<string>(maxLength: 500, nullable: true),
                BenefitScoreAtDecision  = table.Column<decimal>(precision: 8, scale: 2, nullable: false),
                ConfidenceAtDecision    = table.Column<decimal>(precision: 5, scale: 2, nullable: false),
                MilesBefore             = table.Column<decimal>(precision: 10, scale: 2, nullable: true),
                MilesAfter              = table.Column<decimal>(precision: 10, scale: 2, nullable: true),
                DriveMinutesBefore      = table.Column<int>(nullable: true),
                DriveMinutesAfter       = table.Column<int>(nullable: true),
                ActuallyLate            = table.Column<bool>(nullable: true),
                ActualMiles             = table.Column<decimal>(precision: 10, scale: 2, nullable: true),
                RouteCompleted          = table.Column<bool>(nullable: true),
                OutcomeNote             = table.Column<string>(maxLength: 200, nullable: true),
                OutcomeRecordedAtUtc    = table.Column<DateTimeOffset>(nullable: true),
                OptimiserVersion        = table.Column<string>(maxLength: 40, nullable: false),
                ScoringConfigVersion    = table.Column<string>(maxLength: 40, nullable: true),
                CorrelationId           = table.Column<Guid>(nullable: false),
                RecordedAtUtc           = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_OptimisationDecisionHistory", x => x.HistoryId));

        migrationBuilder.CreateIndex("IX_OptimisationDecisionHistory_PlanningDate",
            "OptimisationDecisionHistory", "PlanningDate");
        migrationBuilder.CreateIndex("IX_OptimisationDecisionHistory_AnalysisId",
            "OptimisationDecisionHistory", "AnalysisId");
        migrationBuilder.CreateIndex("IX_OptimisationDecisionHistory_Kind_Decision",
            "OptimisationDecisionHistory", ["Kind", "Decision"]);
        migrationBuilder.CreateIndex("IX_OptimisationDecisionHistory_CollectionSite_DeliverySite",
            "OptimisationDecisionHistory", ["CollectionSite", "DeliverySite"]);

        // ── OptimisationAuditEvents ───────────────────────────────────────────
        migrationBuilder.CreateTable(
            name: "OptimisationAuditEvents",
            columns: table => new
            {
                EventId          = table.Column<Guid>(nullable: false),
                AnalysisId       = table.Column<Guid>(nullable: false),
                SuggestionId     = table.Column<Guid>(nullable: true),
                CorrelationId    = table.Column<Guid>(nullable: false),
                Action           = table.Column<string>(maxLength: 60, nullable: false),
                PlanningDate     = table.Column<DateOnly>(nullable: false),
                Actor            = table.Column<string>(maxLength: 200, nullable: true),
                OccurredAtUtc    = table.Column<DateTimeOffset>(nullable: false),
                BeforeStateJson  = table.Column<string>(nullable: false),
                AfterStateJson   = table.Column<string>(nullable: false),
                Note             = table.Column<string>(maxLength: 500, nullable: true),
                OptimiserVersion = table.Column<string>(maxLength: 40, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OptimisationAuditEvents", x => x.EventId);
                table.ForeignKey("FK_OptimisationAuditEvents_Analyses",
                    x => x.AnalysisId, "OptimisationAnalyses", "AnalysisId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_OptimisationAuditEvents_AnalysisId",
            "OptimisationAuditEvents", "AnalysisId");
        migrationBuilder.CreateIndex("IX_OptimisationAuditEvents_OccurredAtUtc",
            "OptimisationAuditEvents", "OccurredAtUtc");
        migrationBuilder.CreateIndex("IX_OptimisationAuditEvents_CorrelationId",
            "OptimisationAuditEvents", "CorrelationId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("OptimisationAuditEvents");
        migrationBuilder.DropTable("OptimisationSuggestions");
        migrationBuilder.DropTable("OptimisationDecisionHistory");
        migrationBuilder.DropTable("OptimisationAnalyses");
        migrationBuilder.DropTable("ScoringConfigurations");
    }
}
