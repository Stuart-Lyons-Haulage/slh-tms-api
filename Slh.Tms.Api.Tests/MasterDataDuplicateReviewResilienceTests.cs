using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDataDuplicateReviewResilienceTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public MasterDataDuplicateReviewResilienceTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Site_scan_surfaces_same_external_code_when_address_is_missing()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.AddRange(
                new Site { ExternalCode = $"DUP{suffix}", Name = $"NWF Site {suffix}", Active = true },
                new Site { ExternalCode = $"DUP{suffix}", Name = $"NWF Site {suffix}", DriverTextName = $"NWF Site {suffix}", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.GetAsync("/api/v1/operational-master-data/duplicates?entityType=sites");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = await response.Content.ReadFromJsonAsync<List<MasterDataDuplicateCandidate>>();
        var candidate = Assert.Single(candidates!.Where(x => x.Canonical.Code == $"DUP{suffix}"));
        Assert.True(candidate.CanAutoMerge);
        Assert.True(candidate.Confidence >= 95);
    }

    [Fact]
    public async Task Driver_scan_links_tachomaster_row_to_existing_employee_number_row()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Drivers.AddRange(
                new Driver { EmployeeNumber = $"EMP{suffix}", DisplayName = $"Driver {suffix}", Active = true },
                new Driver { EmployeeNumber = $"EMP{suffix}", DisplayName = $"Driver {suffix}", TachoMasterDriverId = $"TM{suffix}", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.GetAsync("/api/v1/operational-master-data/duplicates?entityType=drivers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = await response.Content.ReadFromJsonAsync<List<MasterDataDuplicateCandidate>>();
        var candidate = Assert.Single(candidates!.Where(x => x.Canonical.Code == $"EMP{suffix}"));
        Assert.True(candidate.CanAutoMerge);
        Assert.True(candidate.Confidence >= 95);
    }

    [Fact]
    public async Task Trailer_scan_matches_slh_numeric_aliases()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Trailers.AddRange(
                new Trailer { TrailerNumber = "1", Type = "Urban", Active = true },
                new Trailer { TrailerNumber = "SLH001", StandardCapacity = 26, Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.GetAsync("/api/v1/operational-master-data/duplicates?entityType=trailers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = await response.Content.ReadFromJsonAsync<List<MasterDataDuplicateCandidate>>();
        Assert.Contains(candidates!, x => x.Canonical.Code is "1" or "SLH001" && x.Duplicates.Any(row => row.Code is "1" or "SLH001"));
    }

    [Fact]
    public async Task Vehicle_merge_reassigns_live_loads_and_integration_mappings()
    {
        var canonicalId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Vehicles.AddRange(
                new Vehicle { Id = canonicalId, Registration = $"AB{suffix}", FleetNumber = "Fleet 1", Active = true },
                new Vehicle { Id = duplicateId, Registration = $"AB{suffix}", FuelPin = "1234", Active = true });
            db.Loads.Add(new Load { Reference = $"RUN{suffix}", PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow), VehicleId = duplicateId });
            db.IntegrationMappings.Add(new IntegrationMapping { Provider = "Fleetio", ExternalKey = $"fleetio-{suffix}", TmsEntityType = "Vehicle", TmsEntityId = duplicateId, Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsJsonAsync("/api/v1/operational-master-data/duplicates/vehicles/merge", new MasterDataDuplicateMergeRequest(canonicalId, [duplicateId], "test vehicle merge"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var canonical = await verifyDb.Vehicles.FindAsync(canonicalId);
        var duplicate = await verifyDb.Vehicles.FindAsync(duplicateId);
        Assert.Equal("1234", canonical!.FuelPin);
        Assert.False(duplicate!.Active);
        Assert.All(verifyDb.Loads.Where(load => load.Reference == $"RUN{suffix}"), load => Assert.Equal(canonicalId, load.VehicleId));
        Assert.All(verifyDb.IntegrationMappings.Where(mapping => mapping.ExternalKey == $"fleetio-{suffix}"), mapping => Assert.Equal(canonicalId, mapping.TmsEntityId));
    }

    [Fact]
    public async Task Rejected_candidate_does_not_reappear_after_refresh()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.AddRange(
                new Site { ExternalCode = $"RJ{suffix}A", Name = $"Reject Site {suffix}", CollectionAddress = "Unit A, Test Road PO19 1AA", Active = true },
                new Site { ExternalCode = $"RJ{suffix}B", Name = $"Reject Site {suffix}", CollectionAddress = "Unit A Test Road PO19 1AA", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var first = await client.GetFromJsonAsync<List<MasterDataDuplicateCandidate>>("/api/v1/operational-master-data/duplicates?entityType=sites");
        var candidate = Assert.Single(first!.Where(x => x.Canonical.Name.Contains(suffix)));

        var reject = await client.PostAsJsonAsync("/api/v1/operational-master-data/duplicates/reject", new MasterDataDuplicateRejectRequest(candidate.CandidateId, candidate.EntityType, "keep separate test"));
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        var second = await client.GetFromJsonAsync<List<MasterDataDuplicateCandidate>>("/api/v1/operational-master-data/duplicates?entityType=sites");
        Assert.DoesNotContain(second!, x => x.CandidateId == candidate.CandidateId);
    }
}
