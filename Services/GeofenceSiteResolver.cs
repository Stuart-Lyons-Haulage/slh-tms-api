using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Loads only the small, stable slice of Site data needed by geofence matching.
/// This deliberately avoids materialising the full legacy Sites entity so an
/// unrelated optional/missing column cannot take down geofence maintenance.
/// </summary>
public static class GeofenceSiteResolver
{
    public static async Task<List<Site>> LoadActiveSitesAsync(TmsDbContext db, CancellationToken ct)
    {
        return await db.Sites.AsNoTracking()
            .Where(x => x.Active && x.Name != null && x.ExternalCode != null)
            .Select(x => new Site
            {
                Id = x.Id,
                ExternalCode = x.ExternalCode,
                Name = x.Name,
                DriverTextName = x.DriverTextName,
                Active = x.Active
            })
            .ToListAsync(ct);
    }
}
