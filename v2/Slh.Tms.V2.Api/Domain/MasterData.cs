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
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Town { get; set; }
    public string? County { get; set; }
    public string? Postcode { get; set; }
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
    public string? MobileNumber { get; set; }
    public string? DrivingLicenceNumber { get; set; }
    public string? TachoCardNumber { get; set; }
    public string? Skills { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class Vehicle : ICanonicalEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Registration { get; set; }
    public string? FleetNumber { get; set; }
    public string? VehicleType { get; set; }
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
