using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

internal static class PlanningResilience
{
    public static Task<List<Load>> ReadLoadsAsync(TmsDbContext db, DateOnly? date, CancellationToken ct) =>
        Slh.Tms.Api.Controllers.PlanningResilience.ReadLoadsAsync(db, date, ct);

    public static Task<Load?> ReadLoadAsync(TmsDbContext db, Guid id, CancellationToken ct) =>
        Slh.Tms.Api.Controllers.PlanningResilience.ReadLoadAsync(db, id, ct);
}
