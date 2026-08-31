using System.Threading;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Serialises all TachoMaster identity and canonical Driver Master writes within an API instance.
/// The five-minute identity scheduler and the daily canonical pass share the same database rows.
/// </summary>
internal static class TachoMasterSyncGate
{
    internal static readonly SemaphoreSlim Instance = new(1, 1);

    internal static Task WaitAsync(CancellationToken ct) => Instance.WaitAsync(ct);

    internal static void Release() => Instance.Release();
}
