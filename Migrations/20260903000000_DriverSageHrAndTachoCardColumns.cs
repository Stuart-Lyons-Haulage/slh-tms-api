using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slh.Tms.Api.Migrations;

/// <summary>
/// Migration 49.
/// 1. Promotes Driver.TachoCardNumber from [NotMapped] to a real persisted column.
///    This is the single most important fix — without it the sync cannot use card
///    as a strong identity after the in-memory enrichment is dropped.
/// 2. Adds Driver.SageHrEmployeeNumber — the Sage HR payroll number, which differs
///    from TachoMaster MemberCode. This is the bridge that allows the sync to reconcile
///    drivers whose Sage HR number and TachoMaster member number are different values.
/// 3. Adds Driver.TachoMemberNumber (int) as a typed copy of TachoMasterDriverId so
///    integer comparison is possible without string normalisation in every query.
/// </summary>
public partial class DriverSageHrAndTachoCardColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // TachoCardNumber — promote from NotMapped to real column
        migrationBuilder.AddColumn<string>(
            name: "TachoCardNumber",
            table: "Drivers",
            maxLength: 80,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Drivers_TachoCardNumber",
            table: "Drivers",
            column: "TachoCardNumber",
            filter: "[TachoCardNumber] IS NOT NULL");

        // SageHrEmployeeNumber — Sage HR payroll/employee number (separate from TachoMaster MemberCode)
        migrationBuilder.AddColumn<string>(
            name: "SageHrEmployeeNumber",
            table: "Drivers",
            maxLength: 40,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Drivers_SageHrEmployeeNumber",
            table: "Drivers",
            column: "SageHrEmployeeNumber",
            filter: "[SageHrEmployeeNumber] IS NOT NULL");

        // DrivingLicenceNumber — promote from NotMapped to real column
        migrationBuilder.AddColumn<string>(
            name: "DrivingLicenceNumber",
            table: "Drivers",
            maxLength: 80,
            nullable: true);

        // LicenceExpiry — promote from NotMapped to real column
        migrationBuilder.AddColumn<DateOnly>(
            name: "LicenceExpiry",
            table: "Drivers",
            nullable: true);

        // LicenceStatus — promote from NotMapped to real column
        migrationBuilder.AddColumn<string>(
            name: "LicenceStatus",
            table: "Drivers",
            maxLength: 40,
            nullable: true);

        // AgencyName — promote from NotMapped to real column
        migrationBuilder.AddColumn<string>(
            name: "AgencyName",
            table: "Drivers",
            maxLength: 160,
            nullable: true);

        // Notes — promote from NotMapped to real column
        migrationBuilder.AddColumn<string>(
            name: "Notes",
            table: "Drivers",
            maxLength: 500,
            nullable: true);

        // Coding — promote from NotMapped to real column
        migrationBuilder.AddColumn<string>(
            name: "Coding",
            table: "Drivers",
            maxLength: 80,
            nullable: true);

        // TachoDriveAvailableTodayMinutes — promote from NotMapped to real column
        // (populated by tacho sync, reset each day by background job)
        migrationBuilder.AddColumn<int>(
            name: "TachoDriveAvailableTodayMinutes",
            table: "Drivers",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "TachoDriveAvailableWeekMinutes",
            table: "Drivers",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "TachoWorkAvailableWeekMinutes",
            table: "Drivers",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("TachoCardNumber", "Drivers");
        migrationBuilder.DropColumn("SageHrEmployeeNumber", "Drivers");
        migrationBuilder.DropColumn("DrivingLicenceNumber", "Drivers");
        migrationBuilder.DropColumn("LicenceExpiry", "Drivers");
        migrationBuilder.DropColumn("LicenceStatus", "Drivers");
        migrationBuilder.DropColumn("AgencyName", "Drivers");
        migrationBuilder.DropColumn("Notes", "Drivers");
        migrationBuilder.DropColumn("Coding", "Drivers");
        migrationBuilder.DropColumn("TachoDriveAvailableTodayMinutes", "Drivers");
        migrationBuilder.DropColumn("TachoDriveAvailableWeekMinutes", "Drivers");
        migrationBuilder.DropColumn("TachoWorkAvailableWeekMinutes", "Drivers");
        migrationBuilder.DropIndex("IX_Drivers_TachoCardNumber", "Drivers");
        migrationBuilder.DropIndex("IX_Drivers_SageHrEmployeeNumber", "Drivers");
    }
}
