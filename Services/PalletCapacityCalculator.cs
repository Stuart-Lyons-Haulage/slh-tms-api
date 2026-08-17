namespace Slh.Tms.Api.Services;

public static class PalletCapacityCalculator
{
    public const decimal DefaultStandardCapacity = 26m;
    public const decimal DefaultEuroCapacity = 33m;

    public static PalletCapacityResult Calculate(
        decimal? standardPallets,
        decimal? euroPallets,
        decimal? unknownPallets = null,
        decimal standardCapacity = DefaultStandardCapacity,
        decimal euroCapacity = DefaultEuroCapacity)
    {
        if (standardCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(standardCapacity));
        if (euroCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(euroCapacity));
        if (standardPallets < 0 || euroPallets < 0 || unknownPallets < 0)
            throw new ArgumentOutOfRangeException(nameof(standardPallets), "Pallet quantities cannot be negative.");

        var standard = standardPallets ?? 0m;
        var euro = euroPallets ?? 0m;
        var unknown = unknownPallets ?? 0m;
        var utilisation = (standard / standardCapacity) + (euro / euroCapacity);
        var utilisationPercent = Math.Round(utilisation * 100m, 1, MidpointRounding.AwayFromZero);
        var standardEquivalentUsed = Math.Round(utilisation * standardCapacity, 2, MidpointRounding.AwayFromZero);

        var status = unknown > 0
            ? "Amber"
            : utilisation > 1m
                ? "Red"
                : "Green";

        return new PalletCapacityResult(
            standard,
            euro,
            unknown,
            standardCapacity,
            euroCapacity,
            utilisationPercent,
            standardEquivalentUsed,
            standardCapacity,
            status,
            unknown > 0
                ? "Pallet type is missing for part of this load, so capacity cannot be fully confirmed."
                : utilisation > 1m
                    ? "Calculated trailer footprint exceeds available capacity."
                    : "Calculated trailer footprint is within capacity.");
    }
}

public sealed record PalletCapacityResult(
    decimal StandardPallets,
    decimal EuroPallets,
    decimal UnknownPallets,
    decimal StandardCapacity,
    decimal EuroCapacity,
    decimal UtilisationPercent,
    decimal StandardEquivalentUsed,
    decimal StandardEquivalentCapacity,
    string Status,
    string Message);
