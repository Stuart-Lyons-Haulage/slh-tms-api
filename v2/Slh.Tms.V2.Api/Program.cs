using Microsoft.EntityFrameworkCore;
using Slh.Tms.V2.Api.Data;

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
            .Where(x => x.State == Slh.Tms.V2.Api.Domain.IntakeReviewState.NeedsReview)
            .OrderBy(x => x.CreatedAtUtc)
            .Take(250)
            .ToListAsync(ct));

    app.MapGet("/api/v2/orders", async (DateOnly? date, OperationsDbContext db, CancellationToken ct) =>
    {
        var query = db.Orders.AsNoTracking();
        if (date is not null) query = query.Where(x => x.CollectionDate == date);
        return await query.OrderBy(x => x.CollectionDate).ThenBy(x => x.CollectionTime).Take(1000).ToListAsync(ct);
    });
}

app.Run();
