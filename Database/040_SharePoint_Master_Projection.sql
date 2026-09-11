/*
  SLH TMS SharePoint master-data projection
  Step 2 / SQL migration
  Source of truth: SharePoint Hub lists.
  TMS API access: views only; sync writer uses a separate SQL principal.
  Legacy NorthEligible and PreloadEligible are deliberately excluded.
*/

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.master_depots', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_depots
    (
        DepotId nvarchar(40) NOT NULL CONSTRAINT PK_master_depots PRIMARY KEY,
        DepotName nvarchar(200) NOT NULL,
        [Address] nvarchar(500) NULL,
        Postcode nvarchar(20) NULL,
        Latitude decimal(9,6) NULL,
        Longitude decimal(9,6) NULL,
        GeofenceRadiusMetres int NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_depots_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_depots_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_depots_IsActive DEFAULT 1
    );
END;

IF OBJECT_ID(N'dbo.master_customers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_customers
    (
        CustomerId nvarchar(40) NOT NULL CONSTRAINT PK_master_customers PRIMARY KEY,
        CustomerName nvarchar(200) NOT NULL,
        AccountCode nvarchar(40) NULL,
        TradingName nvarchar(200) NULL,
        CustomerAliases nvarchar(max) NULL,
        InvoiceAddress nvarchar(1000) NULL,
        InvoiceEmail nvarchar(320) NULL,
        DefaultContactName nvarchar(200) NULL,
        DefaultContactPhone nvarchar(40) NULL,
        AccountOwner nvarchar(200) NULL,
        ServiceNotes nvarchar(1000) NULL,
        DefaultSiteCode nvarchar(80) NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_customers_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_customers_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_customers_IsActive DEFAULT 1
    );
END;

IF OBJECT_ID(N'dbo.master_drivers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_drivers
    (
        DriverId nvarchar(40) NOT NULL CONSTRAINT PK_master_drivers PRIMARY KEY,
        FullName nvarchar(160) NOT NULL,
        PreferredName nvarchar(160) NULL,
        DisplayName nvarchar(160) NULL,
        TachoName nvarchar(160) NULL,
        MobileNumber nvarchar(40) NULL,
        LicenceNumber nvarchar(80) NULL,
        LicenceExpiry date NULL,
        CPCExpiry date NULL,
        DigitalTachoCardExpiry date NULL,
        MedicalExpiry date NULL,
        TachoCardNumber nvarchar(80) NULL,
        TachoMasterDriverId nvarchar(80) NULL,
        EmploymentType nvarchar(30) NULL,
        AgencyName nvarchar(160) NULL,
        DriverType nvarchar(80) NULL,
        DriverGroup nvarchar(80) NULL,
        Skills nvarchar(160) NULL,
        Coding nvarchar(80) NULL,
        Notes nvarchar(500) NULL,
        DefaultDepotId nvarchar(40) NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_drivers_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_drivers_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_drivers_IsActive DEFAULT 1,
        CONSTRAINT FK_master_drivers_Depot FOREIGN KEY (DefaultDepotId) REFERENCES dbo.master_depots(DepotId)
    );
END;

IF OBJECT_ID(N'dbo.master_vehicles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_vehicles
    (
        VehicleId nvarchar(40) NOT NULL CONSTRAINT PK_master_vehicles PRIMARY KEY,
        FleetNumber nvarchar(40) NULL,
        Registration nvarchar(20) NOT NULL,
        VehicleType nvarchar(80) NULL,
        Abbreviation nvarchar(20) NULL,
        Transmission nvarchar(20) NULL,
        DvsCompliant bit NULL,
        FuelProvider nvarchar(30) NULL,
        CabMobile nvarchar(40) NULL,
        FuelPin nvarchar(80) NULL,
        ShellCard nvarchar(80) NULL,
        BpRedCard nvarchar(80) NULL,
        BpPlainCard nvarchar(80) NULL,
        FuelPinSecretName nvarchar(120) NULL,
        FuelCardLastFour nvarchar(4) NULL,
        FleetioAssetId nvarchar(80) NULL,
        FleetioName nvarchar(160) NULL,
        FleetioStatus nvarchar(80) NULL,
        SamsaraAssetId nvarchar(80) NULL,
        MOTExpiry date NULL,
        TachoCalibrationExpiry date NULL,
        VehicleTestExpiry date NULL,
        Notes nvarchar(500) NULL,
        DefaultDepotId nvarchar(40) NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_vehicles_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_vehicles_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_vehicles_IsActive DEFAULT 1,
        CONSTRAINT FK_master_vehicles_Depot FOREIGN KEY (DefaultDepotId) REFERENCES dbo.master_depots(DepotId)
    );
END;

IF OBJECT_ID(N'dbo.master_trailers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_trailers
    (
        TrailerId nvarchar(40) NOT NULL CONSTRAINT PK_master_trailers PRIMARY KEY,
        FleetNumber nvarchar(40) NULL,
        Registration nvarchar(20) NULL,
        TrailerNumber nvarchar(40) NULL,
        TrailerType nvarchar(80) NULL,
        StandardCapacity int NULL,
        EuroCapacity int NULL,
        MOTExpiry date NULL,
        TestExpiry date NULL,
        DefaultDepotId nvarchar(40) NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_trailers_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_trailers_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_trailers_IsActive DEFAULT 1,
        CONSTRAINT FK_master_trailers_Depot FOREIGN KEY (DefaultDepotId) REFERENCES dbo.master_depots(DepotId)
    );
END;

IF OBJECT_ID(N'dbo.master_sites', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_sites
    (
        SiteId nvarchar(40) NOT NULL CONSTRAINT PK_master_sites PRIMARY KEY,
        SiteName nvarchar(200) NOT NULL,
        CustomerId nvarchar(40) NULL,
        CustomerCode nvarchar(40) NULL,
        [Address] nvarchar(500) NULL,
        Address2 nvarchar(500) NULL,
        Postcode nvarchar(20) NULL,
        Latitude decimal(9,6) NULL,
        Longitude decimal(9,6) NULL,
        GeofenceRadiusMetres int NULL,
        SiteType nvarchar(20) NULL,
        OpenTime time(0) NULL,
        CloseTime time(0) NULL,
        SpecialInstructions nvarchar(1000) NULL,
        DriverTextName nvarchar(200) NULL,
        CollectionInstructions nvarchar(1000) NULL,
        MapLink nvarchar(1000) NULL,
        OperationalRegion nvarchar(80) NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_sites_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_sites_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_sites_IsActive DEFAULT 1,
        CONSTRAINT FK_master_sites_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.master_customers(CustomerId)
    );
END;

IF OBJECT_ID(N'dbo.master_subcontractors', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_subcontractors
    (
        SubcontractorId nvarchar(40) NOT NULL CONSTRAINT PK_master_subcontractors PRIMARY KEY,
        CompanyName nvarchar(200) NOT NULL,
        ContactName nvarchar(200) NULL,
        ContactPhone nvarchar(40) NULL,
        ContactEmail nvarchar(320) NULL,
        OperatorLicenceNumber nvarchar(80) NULL,
        OperatorLicenceExpiry date NULL,
        InsuranceExpiry date NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_subcontractors_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_subcontractors_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_subcontractors_IsActive DEFAULT 1
    );
END;

IF OBJECT_ID(N'dbo.master_markets', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_markets
    (
        MarketId nvarchar(160) NOT NULL CONSTRAINT PK_master_markets PRIMARY KEY,
        Market nvarchar(80) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        StandOrLocation nvarchar(200) NULL,
        Salesman nvarchar(200) NULL,
        Sender nvarchar(200) NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_markets_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_markets_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_markets_IsActive DEFAULT 1
    );
END;

IF OBJECT_ID(N'dbo.master_fuel_cards', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_fuel_cards
    (
        FuelCardId nvarchar(80) NOT NULL CONSTRAINT PK_master_fuel_cards PRIMARY KEY,
        VehicleId nvarchar(40) NULL,
        Registration nvarchar(20) NULL,
        FuelProvider nvarchar(30) NULL,
        FuelPinSecretName nvarchar(120) NULL,
        FuelCardLastFour nvarchar(4) NULL,
        ShellCard nvarchar(80) NULL,
        BpRedCard nvarchar(80) NULL,
        BpPlainCard nvarchar(80) NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_fuel_cards_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_fuel_cards_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_fuel_cards_IsActive DEFAULT 1,
        CONSTRAINT FK_master_fuel_cards_Vehicle FOREIGN KEY (VehicleId) REFERENCES dbo.master_vehicles(VehicleId)
    );
END;

IF OBJECT_ID(N'dbo.master_fuel_prices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_fuel_prices
    (
        FuelPriceId nvarchar(160) NOT NULL CONSTRAINT PK_master_fuel_prices PRIMARY KEY,
        WeekCommencing date NOT NULL,
        Provider nvarchar(120) NOT NULL,
        PricePencePerLitre decimal(10,2) NOT NULL,
        IsPricingMaximum bit NOT NULL CONSTRAINT DF_master_fuel_prices_IsMaximum DEFAULT 0,
        [Source] nvarchar(200) NULL,
        Notes nvarchar(500) NULL,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_fuel_prices_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_fuel_prices_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_fuel_prices_IsActive DEFAULT 1
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_customers_AccountCode' AND object_id = OBJECT_ID(N'dbo.master_customers'))
    CREATE UNIQUE INDEX UX_master_customers_AccountCode ON dbo.master_customers(AccountCode) WHERE AccountCode IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_vehicles_Registration' AND object_id = OBJECT_ID(N'dbo.master_vehicles'))
    CREATE UNIQUE INDEX UX_master_vehicles_Registration ON dbo.master_vehicles(Registration);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_drivers_TachoMasterDriverId' AND object_id = OBJECT_ID(N'dbo.master_drivers'))
    CREATE UNIQUE INDEX UX_master_drivers_TachoMasterDriverId ON dbo.master_drivers(TachoMasterDriverId) WHERE TachoMasterDriverId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_trailers_Registration' AND object_id = OBJECT_ID(N'dbo.master_trailers'))
    CREATE UNIQUE INDEX UX_master_trailers_Registration ON dbo.master_trailers(Registration) WHERE Registration IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_markets_Market_Name' AND object_id = OBJECT_ID(N'dbo.master_markets'))
    CREATE UNIQUE INDEX UX_master_markets_Market_Name ON dbo.master_markets(Market, [Name]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_fuel_prices_Week_Provider' AND object_id = OBJECT_ID(N'dbo.master_fuel_prices'))
    CREATE UNIQUE INDEX UX_master_fuel_prices_Week_Provider ON dbo.master_fuel_prices(WeekCommencing, Provider);

COMMIT TRANSACTION;
GO

CREATE OR ALTER VIEW dbo.vw_ActiveDepots AS SELECT * FROM dbo.master_depots WHERE IsActive = 1;
GO
CREATE OR ALTER VIEW dbo.vw_ActiveCustomers AS SELECT * FROM dbo.master_customers WHERE IsActive = 1;
GO
CREATE OR ALTER VIEW dbo.vw_ActiveDrivers AS SELECT * FROM dbo.master_drivers WHERE IsActive = 1;
GO
CREATE OR ALTER VIEW dbo.vw_ActiveVehicles AS SELECT * FROM dbo.master_vehicles WHERE IsActive = 1;
GO
CREATE OR ALTER VIEW dbo.vw_ActiveTrailers AS SELECT * FROM dbo.master_trailers WHERE IsActive = 1;
GO
CREATE OR ALTER VIEW dbo.vw_ActiveSites AS SELECT * FROM dbo.master_sites WHERE IsActive = 1;
GO
CREATE OR ALTER VIEW dbo.vw_ActiveSubcontractors AS SELECT * FROM dbo.master_subcontractors WHERE IsActive = 1;
GO
CREATE OR ALTER VIEW dbo.vw_ActiveMarkets AS SELECT * FROM dbo.master_markets WHERE IsActive = 1;
GO
CREATE OR ALTER VIEW dbo.vw_ActiveFuelCards AS SELECT * FROM dbo.master_fuel_cards WHERE IsActive = 1;
GO
CREATE OR ALTER VIEW dbo.vw_ActiveFuelPrices AS SELECT * FROM dbo.master_fuel_prices WHERE IsActive = 1;
GO

IF DATABASE_PRINCIPAL_ID(N'tms_master_reader') IS NULL
    CREATE ROLE [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveDepots TO [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveCustomers TO [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveDrivers TO [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveVehicles TO [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveTrailers TO [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveSites TO [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveSubcontractors TO [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveMarkets TO [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveFuelCards TO [tms_master_reader];
GRANT SELECT ON OBJECT::dbo.vw_ActiveFuelPrices TO [tms_master_reader];
