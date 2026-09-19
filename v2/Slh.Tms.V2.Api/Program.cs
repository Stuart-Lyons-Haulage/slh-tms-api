using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Application;
using Slh.Tms.V2.Api.Data;
using Slh.Tms.V2.Api.Domain;
using OpenTelemetry.Trace;
using Azure.Monitor.OpenTelemetry.Exporter;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["TMS_V2_SQL_CONNECTION"]
    ?? builder.Configuration.GetConnectionString("TmsV2");

if (!string.IsNullOrWhiteSpace(connectionString))
{
    static void ConfigureSql(DbContextOptionsBuilder options, string connection) =>
        options.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());

    builder.Services.AddDbContext<MasterDataDbContext>(options => ConfigureSql(options, connectionString));
    builder.Services.AddDbContext<IntakeDbContext>(options => ConfigureSql(options, connectionString));
    builder.Services.AddDbContext<OperationsDbContext>(options => ConfigureSql(options, connectionString));

    builder.Services.AddScoped<MasterResolver>();
    builder.Services.AddScoped<OrderPromotionService>();
    builder.Services.AddScoped<MasterDataWorkbookImportService>();
    builder.Services.AddScoped<MasterDataCrudService>();
    builder.Services.AddScoped<MasterDataReviewAllocationService>();
    builder.Services.AddScoped<PlanningService>();

    builder.Services.AddHealthChecks()
        .AddDbContextCheck<MasterDataDbContext>("master-data-db")
        .AddDbContextCheck<IntakeDbContext>("intake-db")
        .AddDbContextCheck<OperationsDbContext>("operations-db");
}
else
{
    builder.Services.AddHealthChecks();
}

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation();
        var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsights))
            tracing.AddAzureMonitorTraceExporter(options => options.ConnectionString = appInsights);
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "SLH TMS V2 API",
    version = "2.0-foundation",
    deployment = "local-first",
    boundaries = new[] { "master", "intake", "ops" },
    utc = DateTimeOffset.UtcNow
}));

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

if (!string.IsNullOrWhiteSpace(connectionString))
{
    app.MapGet("/api/v2/master/customers", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.Customers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Code).ToListAsync(ct));

    app.MapGet("/api/v2/master/sites", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct));

    app.MapGet("/api/v2/master/markets", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.Markets.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct));

    app.MapGet("/api/v2/master/drivers", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.Drivers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.DisplayName).ToListAsync(ct));

    app.MapGet("/api/v2/master/vehicles", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.Vehicles.AsNoTracking()
            .Where(x => x.Active)
            .OrderBy(x => x.Registration)
            .Select(x => new
            {
                x.Id,
                x.Registration,
                x.FleetNumber,
                x.Abbreviation,
                x.VehicleType,
                x.Transmission,
                x.Dvs,
                x.CabMobile,
                x.Notes,
                x.Active
            })
            .ToListAsync(ct));

    app.MapGet("/api/v2/master/fuel-cards", async (MasterDataDbContext db, CancellationToken ct) =>
        await (
            from card in db.FuelCards.AsNoTracking()
            join vehicle in db.Vehicles.AsNoTracking() on card.VehicleId equals vehicle.Id into vehicles
            from vehicle in vehicles.DefaultIfEmpty()
            where card.Active
            orderby vehicle!.Registration, card.Provider, card.CardType
            select new
            {
                card.Id,
                card.VehicleId,
                vehicleRegistration = vehicle == null ? null : vehicle.Registration,
                card.Provider,
                card.CardType,
                card.CardNumber,
                card.Pin,
                card.Notes,
                card.Active
            }).ToListAsync(ct));

    app.MapGet("/api/v2/master/trailers", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.Trailers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.TrailerNumber).ToListAsync(ct));

    app.MapGet("/api/v2/master/customer-contacts", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.CustomerContacts.AsNoTracking().Where(x => x.Active).OrderBy(x => x.ContactName).ToListAsync(ct));

    app.MapGet("/api/v2/master/market-contacts", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.MarketContacts.AsNoTracking().Where(x => x.Active).OrderBy(x => x.MarketName).ThenBy(x => x.Name).ToListAsync(ct));

    app.MapGet("/api/v2/master/site-cutoffs", async (MasterDataDbContext db, CancellationToken ct) =>
        await (
            from cutoff in db.SiteCutoffs.AsNoTracking()
            join site in db.Sites.AsNoTracking() on cutoff.SiteId equals site.Id
            where cutoff.Active && site.Active
            orderby site.Name, cutoff.Plan, cutoff.StandardCutoff
            select new
            {
                cutoff.Id,
                cutoff.Code,
                cutoff.SiteId,
                siteCode = site.Code,
                siteName = site.Name,
                cutoff.Plan,
                cutoff.StandardCutoff,
                cutoff.ExtendedCutoff,
                cutoff.Contact,
                cutoff.Notes,
                cutoff.Temperature,
                cutoff.PalletType,
                cutoff.LastDespatchTime,
                cutoff.PlannedCollectFrom,
                cutoff.PlannedCollectTo,
                cutoff.DepotDeliveryDeadline,
                cutoff.Active
            }).ToListAsync(ct));

    app.MapGet("/api/v2/master/route-times", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.RouteTimings.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Route).ToListAsync(ct));

    app.MapGet("/api/v2/master/fuel-prices", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.FuelPrices.AsNoTracking().Where(x => x.Active).OrderByDescending(x => x.WeekCommencing).Take(2500).ToListAsync(ct));

    app.MapGet("/api/v2/master/alias-candidates", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.SiteAliasCandidates.AsNoTracking().Where(x => x.Active && !x.Approved).OrderBy(x => x.AliasType).ThenBy(x => x.Alias).ToListAsync(ct));

    app.MapGet("/api/v2/master/review", async (MasterDataDbContext db, CancellationToken ct) =>
        await db.MasterDataReviewItems.AsNoTracking()
            .Where(x => x.Active && !x.Resolved)
            .OrderBy(x => x.Category)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(ct));

    app.MapGet("/api/v2/master/review/allocations", async (MasterDataDbContext db, CancellationToken ct) =>
    {
        var rows = new List<object>();

        var persistent = await db.MasterDataReviewItems.AsNoTracking()
            .Where(x => x.Active && !x.Resolved)
            .OrderBy(x => x.Category)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        rows.AddRange(persistent.Select(x => (object)new
        {
            id = x.Id,
            kind = "review",
            category = x.Category,
            summary = x.Summary,
            reference = x.SourceReference,
            source = x.EntityType
        }));

        var aliases = await db.SiteAliasCandidates.AsNoTracking()
            .Where(x => x.Active && !x.Approved)
            .OrderBy(x => x.AliasType)
            .ThenBy(x => x.Alias)
            .ToListAsync(ct);
        rows.AddRange(aliases.Select(x => (object)new
        {
            id = x.Id,
            kind = "alias",
            category = "Site alias",
            summary = $"{x.AliasType} alias: {x.Alias}",
            reference = x.Alias,
            source = x.Source
        }));

        var timings = await db.RouteTimings.AsNoTracking()
            .Where(x => x.Active && x.SiteId == null)
            .OrderBy(x => x.Route)
            .ToListAsync(ct);
        rows.AddRange(timings.Select(x => (object)new
        {
            id = x.Id,
            kind = "routeTiming",
            category = "Planner knowledge",
            summary = x.Route,
            reference = x.PalletType,
            source = "Unallocated route timing"
        }));

        var contacts = await (
            from contact in db.CustomerContacts.AsNoTracking()
            join customer in db.Customers.AsNoTracking() on contact.CustomerId equals customer.Id
            where contact.Active && contact.SiteId == null
            orderby customer.Name, contact.ContactName
            select new
            {
                contact.Id,
                customer.Name,
                contact.ContactName,
                contact.Email,
                contact.Phone
            }).ToListAsync(ct);
        rows.AddRange(contacts.Select(x => (object)new
        {
            id = x.Id,
            kind = "customerContact",
            category = "Customer contact",
            summary = $"{x.Name} · {x.ContactName}",
            reference = x.Email ?? x.Phone,
            source = "Unallocated contact"
        }));

        return Results.Ok(rows);
    });

    app.MapPost("/api/v2/master/review/allocate-site", async (
        SiteAllocationRequest request,
        MasterDataReviewAllocationService allocator,
        CancellationToken ct) =>
    {
        try
        {
            await allocator.AllocateToSiteAsync(request.Kind, request.Id, request.SiteId, ct);
            return Results.Ok(new { allocated = true, request.Kind, request.Id, request.SiteId });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    app.MapGet("/api/v2/master/sites/{id:guid}/crm", async (Guid id, MasterDataDbContext db, CancellationToken ct) =>
    {
        var site = await db.Sites.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (site is null) return Results.NotFound();

        Customer? customer = null;
        if (site.CustomerId is Guid customerId)
            customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct);

        var aliases = await db.SiteAliases.AsNoTracking()
            .Where(x => x.SiteId == id)
            .OrderBy(x => x.Alias)
            .ToListAsync(ct);

        var cutoffs = await db.SiteCutoffs.AsNoTracking()
            .Where(x => x.Active && x.SiteId == id)
            .OrderBy(x => x.Plan)
            .ThenBy(x => x.StandardCutoff)
            .ToListAsync(ct);

        var markets = await db.Markets.AsNoTracking()
            .Where(x => x.Active && x.SiteId == id)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var identities = await db.ExternalIdentities.AsNoTracking()
            .Where(x => x.Active && x.EntityType == "Site" && x.EntityId == id)
            .OrderBy(x => x.Provider)
            .ToListAsync(ct);

        var routeTimes = await db.RouteTimings.AsNoTracking()
            .Where(x => x.Active && x.SiteId == id)
            .OrderBy(x => x.Route)
            .Take(250)
            .ToListAsync(ct);

        var customerContacts = await db.CustomerContacts.AsNoTracking()
            .Where(x => x.Active && x.SiteId == id)
            .OrderBy(x => x.ContactName)
            .ToListAsync(ct);

        var reviewItems = await db.MasterDataReviewItems.AsNoTracking()
            .Where(x => x.Active && !x.Resolved &&
                (x.Summary.Contains(site.Code) || x.Summary.Contains(site.Name)))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            site,
            customer,
            aliases,
            cutoffs,
            markets,
            externalIdentities = identities,
            routeTimes,
            customerContacts,
            reviewItems
        });
    });

    app.MapPut("/api/v2/master/{entity}/{id:guid}", async (
        string entity,
        Guid id,
        JsonElement patch,
        MasterDataCrudService crud,
        CancellationToken ct) =>
    {
        try
        {
            var updated = await crud.UpdateAsync(entity, id, patch, ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    app.MapDelete("/api/v2/master/{entity}/{id:guid}", async (
        string entity,
        Guid id,
        MasterDataCrudService crud,
        CancellationToken ct) =>
    {
        try
        {
            var deleted = await crud.DeleteAsync(entity, id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    });

    app.MapGet("/api/v2/master/summary", async (MasterDataDbContext db, CancellationToken ct) => Results.Ok(new
    {
        customers = await db.Customers.CountAsync(x => x.Active, ct),
        sites = await db.Sites.CountAsync(x => x.Active, ct),
        markets = await db.Markets.CountAsync(x => x.Active, ct),
        drivers = await db.Drivers.CountAsync(x => x.Active, ct),
        vehicles = await db.Vehicles.CountAsync(x => x.Active, ct),
        fuelCards = await db.FuelCards.CountAsync(x => x.Active, ct),
        trailers = await db.Trailers.CountAsync(x => x.Active, ct),
        customerContacts = await db.CustomerContacts.CountAsync(x => x.Active, ct),
        marketContacts = await db.MarketContacts.CountAsync(x => x.Active, ct),
        siteCutoffs = await db.SiteCutoffs.CountAsync(x => x.Active, ct),
        routeTimes = await db.RouteTimings.CountAsync(x => x.Active, ct),
        fuelPrices = await db.FuelPrices.CountAsync(x => x.Active, ct),
        aliasCandidates = await db.SiteAliasCandidates.CountAsync(x => x.Active && !x.Approved, ct),
        reviewItems = await db.MasterDataReviewItems.CountAsync(x => x.Active && !x.Resolved, ct)
    }));

    app.MapPost("/api/v2/master/import/workbook", async (
        HttpRequest request,
        bool? commit,
        MasterDataWorkbookImportService importer,
        CancellationToken ct) =>
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "Upload the master workbook as multipart/form-data." });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "No workbook file was supplied." });

        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "V2 Master Data import currently accepts .xlsx workbooks only." });

        await using var stream = file.OpenReadStream();
        var result = await importer.ImportAsync(stream, commit == true, ct);
        return Results.Ok(result);
    });

    app.MapGet("/api/v2/planning", async (
        DateOnly date,
        PlanningService planning,
        CancellationToken ct) =>
        Results.Ok(await planning.GetSnapshotAsync(date, ct)));

    app.MapPost("/api/v2/planning/runs", async (
        CreatePlanningRunRequest request,
        PlanningService planning,
        CancellationToken ct) =>
    {
        var run = await planning.CreateRunAsync(request, ct);
        return Results.Ok(run);
    });

    app.MapPut("/api/v2/planning/runs/{id:guid}", async (
        Guid id,
        PlanningRunUpdateRequest request,
        PlanningService planning,
        CancellationToken ct) =>
    {
        var run = await planning.UpdateRunAsync(id, request, ct);
        return run is null ? Results.NotFound() : Results.Ok(run);
    });

    app.MapPut("/api/v2/planning/runs/{id:guid}/movement", async (
        Guid id,
        SetMovementQuantityRequest request,
        PlanningService planning,
        CancellationToken ct) =>
    {
        try
        {
            await planning.SetMovementQuantityAsync(id, request, ct);
            return Results.Ok(await planning.GetSnapshotAsync(
                (await planning.GetSnapshotAsync(DateOnly.FromDateTime(DateTime.Today), ct)).PlanDate,
                ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    app.MapGet("/api/v2/intake/review", async (IntakeDbContext db, CancellationToken ct) =>
        await db.IntakeRecords.AsNoTracking()
            .Where(x => x.State == IntakeReviewState.NeedsReview)
            .OrderBy(x => x.CreatedAtUtc)
            .Take(250)
            .ToListAsync(ct));

    app.MapPost("/api/v2/intake/{id:guid}/resolve", async (
        Guid id,
        IntakeDbContext db,
        MasterResolver resolver,
        CancellationToken ct) =>
    {
        var record = await db.IntakeRecords.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (record is null) return Results.NotFound();

        ExtractedOrderDraft? draft;
        try
        {
            draft = JsonSerializer.Deserialize<ExtractedOrderDraft>(
                record.ExtractedJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Extracted order payload is invalid JSON." });
        }

        if (draft is null)
            return Results.BadRequest(new { error = "Extracted order payload is empty." });

        var resolution = await resolver.ResolveAsync(draft, ct);
        record.ResolutionJson = JsonSerializer.Serialize(
            resolution,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        record.Confidence = resolution.Confidence;
        record.ReviewReason = resolution.Issues.Count == 0
            ? null
            : string.Join(" ", resolution.Issues);
        record.State = resolution.RequiresReview
            ? IntakeReviewState.NeedsReview
            : IntakeReviewState.Extracted;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            record.Id,
            record.State,
            record.Confidence,
            resolution
        });
    });

    app.MapPost("/api/v2/intake/{id:guid}/approve", async (
        Guid id,
        IntakeDbContext db,
        CancellationToken ct) =>
    {
        var record = await db.IntakeRecords.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (record is null) return Results.NotFound();

        if (string.IsNullOrWhiteSpace(record.ResolutionJson))
            return Results.BadRequest(new { error = "Resolve the record against Master Data before approval." });

        MasterResolution? resolution;
        try
        {
            resolution = JsonSerializer.Deserialize<MasterResolution>(
                record.ResolutionJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Stored Master Data resolution is invalid." });
        }

        if (resolution is null || resolution.RequiresReview)
            return Results.BadRequest(new
            {
                error = "Unresolved customer/site issues must be corrected before approval.",
                issues = resolution?.Issues
            });

        record.State = IntakeReviewState.Approved;
        record.ReviewReason = null;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { record.Id, record.State });
    });

    app.MapPost("/api/v2/intake/{id:guid}/promote", async (
        Guid id,
        OrderPromotionService promotion,
        CancellationToken ct) =>
    {
        try
        {
            var order = await promotion.PromoteAsync(id, ct);
            return Results.Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    app.MapGet("/api/v2/orders", async (DateOnly? date, OperationsDbContext db, CancellationToken ct) =>
    {
        var query = db.Orders.AsNoTracking();
        if (date is not null) query = query.Where(x => x.CollectionDate == date);
        return await query.OrderBy(x => x.CollectionDate).ThenBy(x => x.CollectionTime).Take(1000).ToListAsync(ct);
    });
}

app.Run();
