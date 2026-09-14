using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slh.Tms.Api.Migrations;

/// <summary>
/// Migration 48.
/// 1. Adds EmailSenderProfiles table — persisted sender→collection site mappings
///    that replace the hardcoded SenderDomainCollectionSites dictionary.
/// 2. Moves Site.Aliases from [NotMapped] to a real persisted column so
///    alias matching is always available without the enrichment round-trip.
/// 3. Seeds the four hardcoded sender domains that currently exist only in code.
/// </summary>
public partial class EmailSenderProfilesAndSiteAliasColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ── 1. Site.Aliases becomes a real column ─────────────────────────────
        migrationBuilder.AddColumn<string>(
            name: "Aliases",
            table: "Sites",
            maxLength: 500,
            nullable: true);

        // ── 2. EmailSenderProfiles ────────────────────────────────────────────
        migrationBuilder.CreateTable(
            name: "EmailSenderProfiles",
            columns: table => new
            {
                Id                = table.Column<Guid>(nullable: false),
                // Matched against SenderAddress (full address or @domain)
                SenderPattern     = table.Column<string>(maxLength: 320, nullable: false),
                // "Email" (exact match) or "Domain" (suffix match)
                PatternType       = table.Column<string>(maxLength: 20, nullable: false, defaultValue: "Domain"),
                CollectionSiteName= table.Column<string>(maxLength: 200, nullable: false),
                // Optional FK-by-name to Sites.Name for display/routing
                CollectionSiteId  = table.Column<Guid>(nullable: true),
                // Customer code this sender typically sends orders for
                DefaultCustomerCode = table.Column<string>(maxLength: 40, nullable: true),
                // Auto-approve orders from this sender when confidence is High
                AutoApprove       = table.Column<bool>(nullable: false, defaultValue: false),
                Notes             = table.Column<string>(maxLength: 500, nullable: true),
                Active            = table.Column<bool>(nullable: false, defaultValue: true),
                CreatedAtUtc      = table.Column<DateTimeOffset>(nullable: false),
                CreatedBy         = table.Column<string>(maxLength: 200, nullable: true),
                UpdatedAtUtc      = table.Column<DateTimeOffset>(nullable: true),
                UpdatedBy         = table.Column<string>(maxLength: 200, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_EmailSenderProfiles", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_EmailSenderProfiles_SenderPattern",
            table: "EmailSenderProfiles",
            column: "SenderPattern");

        migrationBuilder.CreateIndex(
            name: "IX_EmailSenderProfiles_Active",
            table: "EmailSenderProfiles",
            column: "Active");

        // Seed from the hardcoded dictionary that currently lives in EmailOrderIntakeService.cs
        // These rows replace the static SenderDomainCollectionSites dictionary.
        var seed = new[]
        {
            (Guid.NewGuid(), "summerberry.co.uk",      "Summer Berry",   "TSBC"),
            (Guid.NewGuid(), "langmeadherbs.co.uk",    "Ham Farm",       "LANGMEADS"),
            (Guid.NewGuid(), "langmeadfarms.co.uk",    "Ham Farm",       "LANGMEADS"),
            (Guid.NewGuid(), "hillsplants.com",        "Hill Brothers",  "HILLBROTHERS"),
            (Guid.NewGuid(), "doubleh.co.uk",          "Double H",       "DOUBLEH"),
            (Guid.NewGuid(), "barfoots.co.uk",         "Barfoots",       "BARFOOTS"),
            (Guid.NewGuid(), "nwfltd.co.uk",           "NWF",            "NWF"),
        };

        foreach (var (id, domain, site, customer) in seed)
        {
            migrationBuilder.InsertData("EmailSenderProfiles",
                columns: ["Id", "SenderPattern", "PatternType", "CollectionSiteName",
                          "DefaultCustomerCode", "AutoApprove", "Active", "CreatedAtUtc", "CreatedBy"],
                values: [id, domain, "Domain", site, customer, false, true,
                         DateTimeOffset.Parse("2026-09-02T00:00:00+00:00"), "migration"]);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("EmailSenderProfiles");
        migrationBuilder.DropColumn("Aliases", "Sites");
    }
}
