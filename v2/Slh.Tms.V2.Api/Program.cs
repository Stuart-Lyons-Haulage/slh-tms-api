using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Application;
using Slh.Tms.V2.Api.Data;
using Slh.Tms.V2.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("TmsV2")
    ?? builder.Configuration["TMS_V2_SQL_CONNECTION"];

if (!string.IsNullOrWhiteSpace(connectionString))
{
    static void ConfigureSql(DbContextOptionsBuilder options, string connection) =>
        options.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());

    builder.Services.AddDbContext<MasterDataDbContext>(options => ConfigureSql(options, connectionString));
    builder.Services.AddDbContext<IntakeDbContext>(options => ConfigureSql(options, connectionString));
    builder.Services.AddDbContext<OperationsDbContext>(options => ConfigureSql(options, connectionString));

    builder.Services.AddScoped<MasterResolver>();
    builder.Services.AddScoped<OrderPromotionService>();

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
