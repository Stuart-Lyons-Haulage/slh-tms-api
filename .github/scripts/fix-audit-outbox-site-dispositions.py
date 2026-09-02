from pathlib import Path

service = Path("Services/SiteMasterConsolidation.cs")
text = service.read_text()
old = '''        var audits = await db.MasterDataAudits.AsNoTracking()\n            .Where(x => x.EntityType == "Site" &&\n                        (x.Action == "Archived" ||\n                         x.Action == "Restored" ||\n                         x.Action == MergedDuplicateAction))\n            .ToListAsync(ct);\n\n        var result = new Dictionary<Guid, Guid?>();\n'''
new = '''        var audits = await db.MasterDataAudits.AsNoTracking()\n            .Where(x => x.EntityType == "Site" &&\n                        (x.Action == "Archived" ||\n                         x.Action == "Restored" ||\n                         x.Action == MergedDuplicateAction))\n            .ToListAsync(ct);\n\n        // MasterDataAudit writes are now transactionally captured in AuditOutbox and\n        // materialised asynchronously. Reconciliation decisions cannot wait for the\n        // background processor: a durable pending disposition is already authoritative.\n        // Read valid site-disposition payloads directly from the outbox as well as the\n        // materialised audit table, deduplicating by deterministic audit Id below.\n        var pendingAuditPayloads = await db.AuditOutboxes.AsNoTracking()\n            .Where(x => x.EventType == AuditOutboxEventTypes.MasterDataAudit &&\n                        x.ProcessedAt == null)\n            .Select(x => x.Payload)\n            .ToListAsync(ct);\n\n        foreach (var payload in pendingAuditPayloads)\n        {\n            try\n            {\n                var pendingAudit = JsonSerializer.Deserialize<MasterDataAudit>(payload);\n                if (pendingAudit is not null &&\n                    string.Equals(pendingAudit.EntityType, "Site", StringComparison.OrdinalIgnoreCase) &&\n                    (string.Equals(pendingAudit.Action, "Archived", StringComparison.OrdinalIgnoreCase) ||\n                     string.Equals(pendingAudit.Action, "Restored", StringComparison.OrdinalIgnoreCase) ||\n                     string.Equals(pendingAudit.Action, MergedDuplicateAction, StringComparison.OrdinalIgnoreCase)) &&\n                    audits.All(existing => existing.Id != pendingAudit.Id))\n                {\n                    audits.Add(pendingAudit);\n                }\n            }\n            catch (JsonException)\n            {\n                // Poison events are handled by the outbox retry policy. They cannot safely\n                // influence Site restoration decisions until their payload is valid.\n            }\n        }\n\n        var result = new Dictionary<Guid, Guid?>();\n'''
if old not in text:
    raise SystemExit("Archived-site disposition block not found")
service.write_text(text.replace(old, new, 1))

test = Path("Slh.Tms.Api.Tests/SiteMasterConsolidationTests.cs")
text = test.read_text()
if "using Microsoft.Extensions.Logging.Abstractions;" not in text:
    text = text.replace("using Microsoft.EntityFrameworkCore;\n", "using Microsoft.EntityFrameworkCore;\nusing Microsoft.Extensions.Logging.Abstractions;\n", 1)
old_assert = '''        var mergeAudit = Assert.Single(db.MasterDataAudits.Where(x => x.EntityId == duplicate.Id && x.Action == "MergedDuplicate"));\n'''
new_assert = '''        var auditProcessor = new AuditOutboxProcessor(db, NullLogger<AuditOutboxProcessor>.Instance);\n        await auditProcessor.ProcessPendingAsync(CancellationToken.None);\n        var mergeAudit = Assert.Single(db.MasterDataAudits.Where(x => x.EntityId == duplicate.Id && x.Action == "MergedDuplicate"));\n'''
if old_assert not in text:
    raise SystemExit("MergedDuplicate audit assertion not found")
test.write_text(text.replace(old_assert, new_assert, 1))
