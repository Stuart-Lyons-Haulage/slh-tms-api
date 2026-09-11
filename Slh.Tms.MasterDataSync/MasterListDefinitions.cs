namespace Slh.Tms.MasterDataSync;

public sealed record MasterField(string SqlColumn, params string[] SharePointNames);

public sealed record MasterListDefinition(
    string Key,
    string ListName,
    string TableName,
    string AnchorColumn,
    IReadOnlyList<MasterField> Fields)
{
    public static IReadOnlyDictionary<string, MasterListDefinition> All { get; } =
        new Dictionary<string, MasterListDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["depot"] = new("depot", "Depots", "master_depots", "DepotId",
                FieldSet("DepotId", "DepotName", "Address", "Postcode", "Latitude", "Longitude", "GeofenceRadiusMetres", "IsActive")),
            ["customer"] = new("customer", "Hub Customers", "master_customers", "CustomerId",
                FieldSet("CustomerId", "CustomerName", "AccountCode", "TradingName", "CustomerAliases", "InvoiceAddress", "InvoiceEmail", "DefaultContactName", "DefaultContactPhone", "AccountOwner", "ServiceNotes", "DefaultSiteCode", "IsActive")),
            ["driver"] = new("driver", "Hub Drivers", "master_drivers", "DriverId",
                FieldSet("DriverId", "FullName", "PreferredName", "DisplayName", "TachoName", "MobileNumber", "LicenceNumber", "LicenceExpiry", "CPCExpiry", "DigitalTachoCardExpiry", "MedicalExpiry", "TachoCardNumber", "TachoMasterDriverId", "EmploymentType", "AgencyName", "DriverType", "DriverGroup", "Skills", "Coding", "Notes", "DefaultDepotId", "IsActive")),
            ["vehicle"] = new("vehicle", "Hub Vehicles", "master_vehicles", "VehicleId",
                FieldSet("VehicleId", "FleetNumber", "Registration", "VehicleType", "Abbreviation", "Transmission", "DvsCompliant", "FuelProvider", "CabMobile", "FuelPin", "ShellCard", "BpRedCard", "BpPlainCard", "FuelPinSecretName", "FuelCardLastFour", "FleetioAssetId", "FleetioName", "FleetioStatus", "SamsaraAssetId", "MOTExpiry", "TachoCalibrationExpiry", "VehicleTestExpiry", "Notes", "DefaultDepotId", "IsActive")),
            ["trailer"] = new("trailer", "Hub Trailers", "master_trailers", "TrailerId",
                FieldSet("TrailerId", "FleetNumber", "Registration", "TrailerNumber", "TrailerType", "StandardCapacity", "EuroCapacity", "MOTExpiry", "TestExpiry", "DefaultDepotId", "IsActive")),
            ["site"] = new("site", "Hub Sites", "master_sites", "SiteId",
                FieldSet("SiteId", "SiteName", "CustomerId", "CustomerCode", "Address", "Address2", "Postcode", "Latitude", "Longitude", "GeofenceRadiusMetres", "SiteType", "OpenTime", "CloseTime", "SpecialInstructions", "DriverTextName", "CollectionInstructions", "MapLink", "OperationalRegion", "IsActive")),
            ["subcontractor"] = new("subcontractor", "Subcontractors", "master_subcontractors", "SubcontractorId",
                FieldSet("SubcontractorId", "CompanyName", "ContactName", "ContactPhone", "ContactEmail", "OperatorLicenceNumber", "OperatorLicenceExpiry", "InsuranceExpiry", "IsActive")),
            ["market"] = new("market", "TMS Markets", "master_markets", "MarketId",
                FieldSet("MarketId", "Market", "Name", "StandOrLocation", "Salesman", "Sender", "IsActive")),
            ["fuelcard"] = new("fuelcard", "Fuel Cards", "master_fuel_cards", "FuelCardId",
                FieldSet("FuelCardId", "VehicleId", "Registration", "FuelProvider", "FuelPinSecretName", "FuelCardLastFour", "ShellCard", "BpRedCard", "BpPlainCard", "IsActive")),
            ["fuelprice"] = new("fuelprice", "Fuel Pricing", "master_fuel_prices", "FuelPriceId",
                FieldSet("FuelPriceId", "WeekCommencing", "Provider", "PricePencePerLitre", "IsPricingMaximum", "Source", "Notes", "IsActive"))
        };

    private static IReadOnlyList<MasterField> FieldSet(params string[] names) =>
        names.Select(name => new MasterField(name, Aliases(name))).ToArray();

    private static string[] Aliases(string name) => name switch
    {
        "DriverId" => ["DriverId", "DriverKey", "EmployeeNumber"],
        "FullName" => ["FullName", "DriverName", "Title"],
        "CustomerId" => ["CustomerId", "CustomerKey", "AccountCode", "CustomerCode"],
        "CustomerName" => ["CustomerName", "TradingName", "Title"],
        "VehicleId" => ["VehicleId", "VehicleKey", "Title"],
        "TrailerId" => ["TrailerId", "TrailerKey", "Title"],
        "SiteId" => ["SiteId", "SiteKey", "Title"],
        "SiteName" => ["SiteName", "BuildingName", "Title"],
        "DefaultDepotId" => ["DefaultDepotId", "DefaultDepotLookupId", "DefaultDepot"],
        "MarketId" => ["MarketId", "MarketKey", "Title"],
        "FuelCardId" => ["FuelCardId", "FuelCardKey", "Title"],
        "FuelPriceId" => ["FuelPriceId", "FuelPriceKey", "Title"],
        "IsActive" => ["IsActive", "Active"],
        _ => [name]
    };
}
