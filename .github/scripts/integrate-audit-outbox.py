from pathlib import Path

# Reapply the audit-outbox delta onto current main after a normal merge.

db = Path("Data/TmsDbContext.cs")
text = db.read_text()
if "using System.Text.Json;" not in text:
    text = text.replace("using Microsoft.EntityFrameworkCore;\n", "using System.Text.Json;\nusing Microsoft.EntityFrameworkCore;\n", 1)

master_dbset = "    public DbSet<MasterDataAudit> MasterDataAudits => Set<MasterDataAudit>();\n"
if "public DbSet<AuditOutbox> AuditOutboxes" not in text:
    if master_dbset not in text:
        raise SystemExit("MasterDataAudit DbSet marker not found")
    text = text.replace(master_dbset, master_dbset + "    public DbSet<AuditOutbox> AuditOutboxes => Set<AuditOutbox>();\n", 1)

old_save = '''        try\n        {\n            return await base.SaveChangesAsync(cancellationToken);\n        }\n        catch (DbUpdateException ex) when (AuditStorageUnavailable(ex) && ChangeTracker.Entries<MasterDataAudit>().Any(entry => entry.State == EntityState.Added))\n        {\n            // Master-data amendments are operationally authoritative. If the audit table is\n            // missing/lagging in Azure SQL, do not roll back the real edit: remove only the\n            // audit insert and retry the same unit of work.\n            foreach (var entry in ChangeTracker.Entries<MasterDataAudit>().Where(entry => entry.State == EntityState.Added).ToList())\n                entry.State = EntityState.Detached;\n\n            return await base.SaveChangesAsync(cancellationToken);\n        }\n    }\n\n    private static bool AuditStorageUnavailable(Exception exception)\n    {\n        var message = exception.GetBaseException().Message;\n        return message.Contains("MasterDataAudits", StringComparison.OrdinalIgnoreCase)\n            || message.Contains("MasterDataAudit", StringComparison.OrdinalIgnoreCase)\n            || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)\n            || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase)\n            || message.Contains("does not exist or you do not have permissions", StringComparison.OrdinalIgnoreCase)\n            || message.Contains("permission was denied", StringComparison.OrdinalIgnoreCase);\n    }\n'''
new_save = '''        EnqueuePendingMasterDataAudits();\n        return await base.SaveChangesAsync(cancellationToken);\n    }\n\n    internal Task<int> SaveAuditReplayChangesAsync(CancellationToken cancellationToken = default) =>\n        base.SaveChangesAsync(cancellationToken);\n\n    private void EnqueuePendingMasterDataAudits()\n    {\n        var pendingAudits = ChangeTracker.Entries<MasterDataAudit>()\n            .Where(entry => entry.State == EntityState.Added)\n            .ToList();\n\n        foreach (var entry in pendingAudits)\n        {\n            var audit = entry.Entity;\n            AuditOutboxes.Add(new AuditOutbox\n            {\n                EventType = AuditOutboxEventTypes.MasterDataAudit,\n                Payload = JsonSerializer.Serialize(audit),\n                CreatedAt = DateTimeOffset.UtcNow,\n                RetryCount = 0\n            });\n\n            entry.State = EntityState.Detached;\n        }\n    }\n'''
if "EnqueuePendingMasterDataAudits();" not in text:
    if old_save not in text:
        raise SystemExit("Current-main audit fallback block not found")
    text = text.replace(old_save, new_save, 1)

model_marker = '''        b.Entity<MasterDataAudit>()\n            .HasIndex(x => x.ChangedAtUtc)\n            .HasDatabaseName("IX_MasterDataAudits_ChangedAtUtc");\n'''
outbox_model = '''\n        b.Entity<AuditOutbox>().ToTable("AuditOutbox");\n        b.Entity<AuditOutbox>().HasKey(x => x.OutboxId);\n        b.Entity<AuditOutbox>()\n            .HasIndex(x => new { x.ProcessedAt, x.FailedAt, x.CreatedAt })\n            .HasDatabaseName("IX_AuditOutbox_Pending");\n        b.Entity<AuditOutbox>()\n            .HasIndex(x => x.CreatedAt)\n            .HasDatabaseName("IX_AuditOutbox_CreatedAt");\n'''
if 'b.Entity<AuditOutbox>().ToTable("AuditOutbox")' not in text:
    if model_marker not in text:
        raise SystemExit("MasterDataAudit model marker not found")
    text = text.replace(model_marker, model_marker + outbox_model, 1)
db.write_text(text)

program = Path("Program.cs")
text = program.read_text()
hosted_marker = "builder.Services.AddHostedService<DriverMasterClassificationBackgroundService>();\n"
if "AddHostedService<AuditOutboxBackgroundService>()" not in text:
    if hosted_marker not in text:
        raise SystemExit("Hosted service marker not found")
    text = text.replace(hosted_marker, hosted_marker + "builder.Services.AddHostedService<AuditOutboxBackgroundService>();\n", 1)
program.write_text(text)

# Renumber the outbox SQL so canonical planning remains immutable migration 44.
old_sql = Path("Database/039_Audit_Outbox.sql")
new_sql = Path("Database/040_Audit_Outbox.sql")
if old_sql.exists() and not new_sql.exists():
    old_sql.rename(new_sql)
elif not new_sql.exists():
    raise SystemExit("Audit outbox SQL migration source not found")

runner = Path("Services/SchemaMigrationRunner.cs")
text = runner.read_text()
old_tail = '        "039_Canonical_Relational_Planning.sql"\n    ];'
new_tail = '        "039_Canonical_Relational_Planning.sql",\n        "040_Audit_Outbox.sql"\n    ];'
if '"040_Audit_Outbox.sql"' not in text:
    if old_tail not in text:
        raise SystemExit("SchemaMigrationRunner append marker not found")
    text = text.replace(old_tail, new_tail, 1)
runner.write_text(text)

schema_tests = Path("Slh.Tms.Api.Tests/SchemaResourceTests.cs")
text = schema_tests.read_text()
resource_marker = '        Assert.Contains("Slh.Tms.Api.Database.039_Canonical_Relational_Planning.sql", resources);\n'
if "Slh.Tms.Api.Database.040_Audit_Outbox.sql" not in text:
    if resource_marker not in text:
        raise SystemExit("Schema resource 039 assertion marker not found")
    text = text.replace(resource_marker, resource_marker + '        Assert.Contains("Slh.Tms.Api.Database.040_Audit_Outbox.sql", resources);\n', 1)
text = text.replace("        Assert.Equal(44, migrations.Count);", "        Assert.Equal(45, migrations.Count);")
old_assertions = '''        Assert.Equal("037_Driver_Tacho_Identity.sql", migrations[^3].Name);\n        Assert.Equal("038_Driver_Tacho_Identity_Repair.sql", migrations[^2].Name);\n        Assert.Equal("039_Canonical_Relational_Planning.sql", migrations[^1].Name);'''
new_assertions = '''        Assert.Equal("037_Driver_Tacho_Identity.sql", migrations[^4].Name);\n        Assert.Equal("038_Driver_Tacho_Identity_Repair.sql", migrations[^3].Name);\n        Assert.Equal("039_Canonical_Relational_Planning.sql", migrations[^2].Name);\n        Assert.Equal("040_Audit_Outbox.sql", migrations[^1].Name);'''
if '040_Audit_Outbox.sql", migrations[^1]' not in text:
    if old_assertions not in text:
        raise SystemExit("Schema migration tail assertions not found")
    text = text.replace(old_assertions, new_assertions, 1)
schema_tests.write_text(text)

# Update existing tests whose audit assertion is now asynchronous through the outbox.
for filename, assertion, replacement in [
    (
        "Slh.Tms.Api.Tests/GeofenceSitePromotionTests.cs",
        '        Assert.Contains(finalDb.MasterDataAudits, x => x.EntityType == "Site" && x.EntityId == site.Id && x.Action == "CreatedFromOperationalGeofence");',
        '        var auditProcessor = new AuditOutboxProcessor(finalDb, NullLogger<AuditOutboxProcessor>.Instance);\n        await auditProcessor.ProcessPendingAsync(CancellationToken.None);\n        Assert.Contains(finalDb.MasterDataAudits, x => x.EntityType == "Site" && x.EntityId == site.Id && x.Action == "CreatedFromOperationalGeofence");'
    ),
    (
        "Slh.Tms.Api.Tests/OrderReviewSiteMatchTests.cs",
        '        Assert.Contains(verifyDb.MasterDataAudits, row => row.EntityType == "Geofence" && row.EntityId == geofenceId && row.Action == "DeliveryImportSiteConfirmed");',
        '        var auditProcessor = new AuditOutboxProcessor(verifyDb, NullLogger<AuditOutboxProcessor>.Instance);\n        await auditProcessor.ProcessPendingAsync(CancellationToken.None);\n        Assert.True(await verifyDb.MasterDataAudits.AnyAsync(row =>\n            row.EntityType == "Geofence"\n            && row.EntityId == geofenceId\n            && row.Action == "DeliveryImportSiteConfirmed"));'
    )
]:
    path = Path(filename)
    current = path.read_text()
    if "Microsoft.Extensions.Logging.Abstractions" not in current:
        using_marker = "using Microsoft.Extensions.DependencyInjection;\n"
        if using_marker not in current:
            raise SystemExit(f"Logging using marker not found in {filename}")
        current = current.replace(using_marker, using_marker + "using Microsoft.Extensions.Logging.Abstractions;\n", 1)
    if "AuditOutboxProcessor" not in current:
        if assertion not in current:
            raise SystemExit(f"Audit assertion marker not found in {filename}")
        current = current.replace(assertion, replacement, 1)
    path.write_text(current)
