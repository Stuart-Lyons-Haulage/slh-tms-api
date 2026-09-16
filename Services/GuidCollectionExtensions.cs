namespace Slh.Tms.Api.Services;

internal static class GuidCollectionExtensions
{
    public static bool Contains(this IReadOnlyCollection<Guid> values, Guid? value) =>
        value.HasValue && values.Contains(value.Value);
}
