from pathlib import Path

# Reapply PR #280's shared-file deltas onto current main after a normal merge,
# and append the distributed lease to the authoritative schema catalogue.

program = Path("Program.cs")
text = program.read_text()
scoped_marker = "builder.Services.AddScoped<AzureSmsDispatchService>();\n"
if "builder.Services.AddScoped<DistributedLeaseManager>();" not in text:
    if scoped_marker not in text:
        raise SystemExit("AzureSmsDispatchService registration marker not found")
    text = text.replace(scoped_marker, scoped_marker + "builder.Services.AddScoped<DistributedLeaseManager>();\n", 1)
text = text.replace("builder.Services.AddHostedService<IntegrationBackgroundSyncService>();\n", "")
text = text.replace("builder.Services.AddHostedService<TachoCanonicalDriverMasterDailyBackgroundService>();\n", "")
if "builder.Services.AddHostedService<AuditOutboxBackgroundService>();" not in text:
    raise SystemExit("AuditOutboxBackgroundService must be preserved")
program.write_text(text)

ci = Path(".github/workflows/ci.yml")
text = ci.read_text()
api_restore = "          dotnet restore Slh.Tms.Api.csproj\n"
if "dotnet restore Slh.Tms.Jobs/Slh.Tms.Jobs.csproj" not in text:
    if api_restore not in text:
        raise SystemExit("CI API restore marker not found")
    text = text.replace(api_restore, api_restore + "          dotnet restore Slh.Tms.Jobs/Slh.Tms.Jobs.csproj\n", 1)
api_build = "      - name: Build API project (Release)\n        run: dotnet build Slh.Tms.Api.csproj --configuration Release --no-restore\n"
job_build = "\n      - name: Build scheduled Jobs project (Release)\n        run: dotnet build Slh.Tms.Jobs/Slh.Tms.Jobs.csproj --configuration Release --no-restore\n\n      - name: Validate Container Apps Jobs Bicep\n        run: az bicep build --file infra/container-app-jobs.bicep --stdout > /dev/null\n"
if "Build scheduled Jobs project (Release)" not in text:
    if api_build not in text:
        raise SystemExit("CI API build marker not found")
    text = text.replace(api_build, api_build + job_build, 1)
ci.write_text(text)

project = Path("Slh.Tms.Api.csproj")
text = project.read_text()
test_remove = '    <Compile Remove="Slh.Tms.Api.Tests/**/*.cs" />\n'
if 'Compile Remove="Slh.Tms.Jobs/**/*.cs"' not in text:
    if test_remove not in text:
        raise SystemExit("API project test exclusion marker not found")
    text = text.replace(test_remove, test_remove + '    <Compile Remove="Slh.Tms.Jobs/**/*.cs" />\n', 1)
project.write_text(text)

old_sql = Path("Database/040_Distributed_Integration_Lease.sql")
new_sql = Path("Database/041_Distributed_Integration_Lease.sql")
if old_sql.exists() and not new_sql.exists():
    old_sql.rename(new_sql)
elif not new_sql.exists():
    raise SystemExit("Distributed lease SQL migration source not found")

# Replace exact old migration filename references in tracked text artifacts.
for root in [Path("docs"), Path("infra"), Path("Slh.Tms.Api.Tests")]:
    if not root.exists():
        continue
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        try:
            body = path.read_text()
        except UnicodeDecodeError:
            continue
        if "040_Distributed_Integration_Lease.sql" in body:
            path.write_text(body.replace("040_Distributed_Integration_Lease.sql", "041_Distributed_Integration_Lease.sql"))

runner = Path("Services/SchemaMigrationRunner.cs")
text = runner.read_text()
old_tail = '        "040_Audit_Outbox.sql"\n    ];'
new_tail = '        "040_Audit_Outbox.sql",\n        "041_Distributed_Integration_Lease.sql"\n    ];'
if '"041_Distributed_Integration_Lease.sql"' not in text:
    if old_tail not in text:
        raise SystemExit("SchemaMigrationRunner migration-45 tail marker not found")
    text = text.replace(old_tail, new_tail, 1)
runner.write_text(text)

schema_tests = Path("Slh.Tms.Api.Tests/SchemaResourceTests.cs")
text = schema_tests.read_text()
resource_marker = '        Assert.Contains("Slh.Tms.Api.Database.040_Audit_Outbox.sql", resources);\n'
if "Slh.Tms.Api.Database.041_Distributed_Integration_Lease.sql" not in text:
    if resource_marker not in text:
        raise SystemExit("Schema resource migration-45 marker not found")
    text = text.replace(resource_marker, resource_marker + '        Assert.Contains("Slh.Tms.Api.Database.041_Distributed_Integration_Lease.sql", resources);\n', 1)
text = text.replace("        Assert.Equal(45, migrations.Count);", "        Assert.Equal(46, migrations.Count);")
old_assertions = '''        Assert.Equal("037_Driver_Tacho_Identity.sql", migrations[^4].Name);\n        Assert.Equal("038_Driver_Tacho_Identity_Repair.sql", migrations[^3].Name);\n        Assert.Equal("039_Canonical_Relational_Planning.sql", migrations[^2].Name);\n        Assert.Equal("040_Audit_Outbox.sql", migrations[^1].Name);'''
new_assertions = '''        Assert.Equal("037_Driver_Tacho_Identity.sql", migrations[^5].Name);\n        Assert.Equal("038_Driver_Tacho_Identity_Repair.sql", migrations[^4].Name);\n        Assert.Equal("039_Canonical_Relational_Planning.sql", migrations[^3].Name);\n        Assert.Equal("040_Audit_Outbox.sql", migrations[^2].Name);\n        Assert.Equal("041_Distributed_Integration_Lease.sql", migrations[^1].Name);'''
if '041_Distributed_Integration_Lease.sql", migrations[^1]' not in text:
    if old_assertions not in text:
        raise SystemExit("Schema migration tail assertions not found")
    text = text.replace(old_assertions, new_assertions, 1)
schema_tests.write_text(text)
