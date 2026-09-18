namespace Slh.Tms.V2.Api.Domain;

public interface ICanonicalEntity
{
    Guid Id { get; }
    bool Active { get; set; }
}

public sealed class Customer : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class Site : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public Guid? CustomerId { get; set; }
    public string? DriverTextName { get; set; }
    public string? FullAddress { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Town { get; set; }
    public string? County { get; set; }
    public string? Postcode { get; set; }
    public string? MapLink { get; set; }
    public string? CollectionInstructions { get; set; }
    public string? DriverInstructions { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class Market : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public Guid SiteId { get; set; }
    public string? DefaultInstructions { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class Driver : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DisplayName { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? TachoName { get; set; }
    public string? MobileNumber { get; set; }
    public string? DriverType { get; set; }
    public string? DriverGroup { get; set; }
    public string? Skills { get; set; }
    public string? Coding { get; set; }
    public string? AgencyName { get; set; }
    public bool? NorthEligible { get; set; }
    public bool? PreloadEligible { get; set; }
    public string? Notes { get; set; }
    public string? TachoMasterDriverId { get; set; }
    public string? DrivingLicenceNumber { get; set; }
    public DateOnly? LicenceExpiry { get; set; }
    public string? LicenceStatus { get; set; }
    public DateTimeOffset? LastTachoMasterSync { get; set; }
    public string? TachoCardNumber { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class Vehicle : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Registration { get; set; }
    public string? FleetNumber { get; set; }
    public string? Abbreviation { get; set; }
    public string? VehicleType { get; set; }
    public string? Transmission { get; set; }
    public string? Dvs { get; set; }
    public string? CabMobile { get; set; }
    public string? FuelPin { get; set; }
    public string? ShellCard { get; set; }
    public string? BpRedCard { get; set; }
    public string? BpPlainCard { get; set; }
    public string? Notes { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class Trailer : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TrailerNumber { get; set; }
    public string? TrailerType { get; set; }
    public int? PalletCapacity { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class CustomerContact : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Code { get; set; }
    public Guid CustomerId { get; set; }
    public required string ContactName { get; set; }
    public string? Role { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class MarketContact : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Key { get; set; }
    public required string MarketName { get; set; }
    public required string Name { get; set; }
    public string? StandOrLocation { get; set; }
    public string? Salesman { get; set; }
    public string? Sender { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class SiteCutoff : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Code { get; set; }
    public Guid SiteId { get; set; }
    public string? Plan { get; set; }
    public TimeOnly? StandardCutoff { get; set; }
    public TimeOnly? ExtendedCutoff { get; set; }
    public string? Contact { get; set; }
    public string? Notes { get; set; }
    public string? Temperature { get; set; }
    public string? PalletType { get; set; }
    public TimeOnly? LastDespatchTime { get; set; }
    public TimeOnly? PlannedCollectFrom { get; set; }
    public TimeOnly? PlannedCollectTo { get; set; }
    public TimeOnly? DepotDeliveryDeadline { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class RouteTiming : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Key { get; set; }
    public required string Route { get; set; }
    public string? PalletType { get; set; }
    public TimeOnly? LastDespatchTime { get; set; }
    public TimeOnly? PlannedCollectFrom { get; set; }
    public TimeOnly? PlannedCollectTo { get; set; }
    public TimeOnly? DepotDeliveryDeadline { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class FuelPrice : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Code { get; set; }
    public DateOnly WeekCommencing { get; set; }
    public required string Provider { get; set; }
    public decimal PricePencePerLitre { get; set; }
    public bool IsPricingMaximum { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class SiteAliasCandidate : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Alias { get; set; }
    public required string AliasType { get; set; }
    public string? Source { get; set; }
    public Guid? SiteId { get; set; }
    public bool Approved { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class MasterDataReviewItem : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Key { get; set; }
    public required string Category { get; set; }
    public required string EntityType { get; set; }
    public string? SourceReference { get; set; }
    public required string Summary { get; set; }
    public string? PayloadJson { get; set; }
    public bool Resolved { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Active { get; set; } = true;
}

public sealed class ExternalIdentity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Provider { get; set; }
    public required string EntityType { get; set; }
    public required Guid EntityId { get; set; }
    public required string ExternalKey { get; set; }
    public string? ExternalDisplayName { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class SiteAlias
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SiteId { get; set; }
    public required string Alias { get; set; }
    public string? Source { get; set; }
    public bool Approved { get; set; } = true;
}
