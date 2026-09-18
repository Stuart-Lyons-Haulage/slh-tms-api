SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF DB_ID(N'SLH_TMS_V2_DEV') IS NULL
BEGIN
    CREATE DATABASE [SLH_TMS_V2_DEV];
END
GO

USE [SLH_TMS_V2_DEV];
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'master') EXEC('CREATE SCHEMA [master]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'intake') EXEC('CREATE SCHEMA [intake]');
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'ops') EXEC('CREATE SCHEMA [ops]');
GO

IF OBJECT_ID(N'[master].[Customers]', N'U') IS NULL
CREATE TABLE [master].[Customers](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Code] nvarchar(40) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_Customers_Code] UNIQUE ([Code])
);
GO

IF OBJECT_ID(N'[master].[Sites]', N'U') IS NULL
CREATE TABLE [master].[Sites](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Code] nvarchar(80) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [CustomerId] uniqueidentifier NULL,
    [AddressLine1] nvarchar(max) NULL,
    [AddressLine2] nvarchar(max) NULL,
    [Town] nvarchar(max) NULL,
    [County] nvarchar(max) NULL,
    [Postcode] nvarchar(20) NULL,
    [DriverInstructions] nvarchar(max) NULL,
    [EarliestCollectionTime] time NULL,
    [LatestCollectionTime] time NULL,
    [EarliestDeliveryTime] time NULL,
    [LatestDeliveryTime] time NULL,
    [StandardCutoff] time NULL,
    [ExtendedCutoff] time NULL,
    [DeadlineContact] nvarchar(200) NULL,
    [DeadlineNotes] nvarchar(max) NULL,
    [Latitude] decimal(9,6) NULL,
    [Longitude] decimal(9,6) NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_Sites_Code] UNIQUE ([Code]),
    CONSTRAINT [FK_master_Sites_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [master].[Customers]([Id])
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_master_Sites_CustomerId' AND object_id = OBJECT_ID(N'[master].[Sites]'))
    CREATE INDEX [IX_master_Sites_CustomerId] ON [master].[Sites]([CustomerId]);
GO

IF OBJECT_ID(N'[master].[Markets]', N'U') IS NULL
CREATE TABLE [master].[Markets](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Code] nvarchar(450) NOT NULL,
    [Name] nvarchar(max) NOT NULL,
    [SiteId] uniqueidentifier NOT NULL,
    [DefaultInstructions] nvarchar(max) NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_Markets_Code] UNIQUE ([Code]),
    CONSTRAINT [FK_master_Markets_Sites_SiteId] FOREIGN KEY ([SiteId]) REFERENCES [master].[Sites]([Id])
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_master_Markets_SiteId' AND object_id = OBJECT_ID(N'[master].[Markets]'))
    CREATE INDEX [IX_master_Markets_SiteId] ON [master].[Markets]([SiteId]);
GO

IF OBJECT_ID(N'[master].[Drivers]', N'U') IS NULL
CREATE TABLE [master].[Drivers](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [DisplayName] nvarchar(max) NOT NULL,
    [EmployeeNumber] nvarchar(450) NULL,
    [MobileNumber] nvarchar(max) NULL,
    [DrivingLicenceNumber] nvarchar(max) NULL,
    [TachoCardNumber] nvarchar(max) NULL,
    [Skills] nvarchar(max) NULL,
    [Active] bit NOT NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_Drivers_EmployeeNumber' AND object_id = OBJECT_ID(N'[master].[Drivers]'))
    CREATE UNIQUE INDEX [UX_master_Drivers_EmployeeNumber] ON [master].[Drivers]([EmployeeNumber]) WHERE [EmployeeNumber] IS NOT NULL;
GO

IF OBJECT_ID(N'[master].[Vehicles]', N'U') IS NULL
CREATE TABLE [master].[Vehicles](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Registration] nvarchar(450) NOT NULL,
    [FleetNumber] nvarchar(max) NULL,
    [VehicleType] nvarchar(max) NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_Vehicles_Registration] UNIQUE ([Registration])
);
GO

IF OBJECT_ID(N'[master].[Trailers]', N'U') IS NULL
CREATE TABLE [master].[Trailers](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [TrailerNumber] nvarchar(450) NOT NULL,
    [Registration] nvarchar(40) NULL,
    [TrailerType] nvarchar(max) NULL,
    [PalletCapacity] int NULL,
    [EuroPalletCapacity] int NULL,
    [CurrentLocation] nvarchar(160) NULL,
    [MotExpiry] date NULL,
    [Notes] nvarchar(max) NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_Trailers_TrailerNumber] UNIQUE ([TrailerNumber])
);
GO

IF OBJECT_ID(N'[master].[ExternalIdentities]', N'U') IS NULL
CREATE TABLE [master].[ExternalIdentities](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Provider] nvarchar(80) NOT NULL,
    [EntityType] nvarchar(80) NOT NULL,
    [EntityId] uniqueidentifier NOT NULL,
    [ExternalKey] nvarchar(200) NOT NULL,
    [ExternalDisplayName] nvarchar(max) NULL,
    [Active] bit NOT NULL
);
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_ExternalIdentities_ActiveKey' AND object_id = OBJECT_ID(N'[master].[ExternalIdentities]'))
    DROP INDEX [UX_master_ExternalIdentities_ActiveKey] ON [master].[ExternalIdentities];

IF COL_LENGTH(N'master.ExternalIdentities', N'Provider') > 160
    ALTER TABLE [master].[ExternalIdentities] ALTER COLUMN [Provider] nvarchar(80) NOT NULL;
IF COL_LENGTH(N'master.ExternalIdentities', N'EntityType') > 160
    ALTER TABLE [master].[ExternalIdentities] ALTER COLUMN [EntityType] nvarchar(80) NOT NULL;
IF COL_LENGTH(N'master.ExternalIdentities', N'ExternalKey') > 400
    ALTER TABLE [master].[ExternalIdentities] ALTER COLUMN [ExternalKey] nvarchar(200) NOT NULL;

CREATE UNIQUE INDEX [UX_master_ExternalIdentities_ActiveKey]
ON [master].[ExternalIdentities]([Provider],[EntityType],[ExternalKey])
WHERE [Active] = 1;
GO

IF OBJECT_ID(N'[master].[SiteAliases]', N'U') IS NULL
CREATE TABLE [master].[SiteAliases](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [SiteId] uniqueidentifier NOT NULL,
    [Alias] nvarchar(450) NOT NULL,
    [Source] nvarchar(max) NULL,
    [Approved] bit NOT NULL,
    CONSTRAINT [FK_master_SiteAliases_Sites_SiteId] FOREIGN KEY ([SiteId]) REFERENCES [master].[Sites]([Id]) ON DELETE CASCADE
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_SiteAliases_Site_Alias' AND object_id = OBJECT_ID(N'[master].[SiteAliases]'))
    CREATE UNIQUE INDEX [UX_master_SiteAliases_Site_Alias] ON [master].[SiteAliases]([SiteId],[Alias]);
GO

IF OBJECT_ID(N'[intake].[Evidence]', N'U') IS NULL
CREATE TABLE [intake].[Evidence](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [SourceSystem] nvarchar(80) NOT NULL,
    [Mailbox] nvarchar(max) NULL,
    [MessageId] nvarchar(450) NULL,
    [Subject] nvarchar(max) NULL,
    [Sender] nvarchar(max) NULL,
    [ReceivedAtUtc] datetimeoffset NOT NULL,
    [EvidenceHash] nvarchar(128) NOT NULL,
    [RawBodyLocation] nvarchar(max) NULL,
    CONSTRAINT [UX_intake_Evidence_Hash] UNIQUE ([EvidenceHash])
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_intake_Evidence_Source_Message' AND object_id = OBJECT_ID(N'[intake].[Evidence]'))
    CREATE INDEX [IX_intake_Evidence_Source_Message] ON [intake].[Evidence]([SourceSystem],[MessageId]);
GO

IF OBJECT_ID(N'[intake].[IntakeRecords]', N'U') IS NULL
CREATE TABLE [intake].[IntakeRecords](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [EvidenceId] uniqueidentifier NOT NULL,
    [ExtractedJson] nvarchar(max) NOT NULL,
    [ResolutionJson] nvarchar(max) NULL,
    [State] int NOT NULL,
    [Confidence] decimal(5,4) NOT NULL,
    [ReviewReason] nvarchar(max) NULL,
    [CreatedAtUtc] datetimeoffset NOT NULL,
    [UpdatedAtUtc] datetimeoffset NOT NULL,
    CONSTRAINT [UX_intake_IntakeRecords_EvidenceId] UNIQUE ([EvidenceId])
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_intake_IntakeRecords_State_Created' AND object_id = OBJECT_ID(N'[intake].[IntakeRecords]'))
    CREATE INDEX [IX_intake_IntakeRecords_State_Created] ON [intake].[IntakeRecords]([State],[CreatedAtUtc]);
GO

IF OBJECT_ID(N'[ops].[Orders]', N'U') IS NULL
CREATE TABLE [ops].[Orders](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [StableKey] nvarchar(250) NOT NULL,
    [CustomerId] uniqueidentifier NOT NULL,
    [CollectionSiteId] uniqueidentifier NOT NULL,
    [DeliverySiteId] uniqueidentifier NOT NULL,
    [MarketId] uniqueidentifier NULL,
    [PurchaseOrder] nvarchar(120) NULL,
    [CustomerOrderReference] nvarchar(160) NULL,
    [SourceOrderReference] nvarchar(160) NULL,
    [CollectionDate] date NOT NULL,
    [CollectionTime] time NULL,
    [DeliveryDate] date NULL,
    [DeliveryTime] time NULL,
    [Pallets] int NOT NULL,
    [Cases] int NOT NULL,
    [Crates] int NOT NULL,
    [Trays] int NOT NULL,
    [TemperatureRequirement] nvarchar(max) NULL,
    [TrailerRequirement] nvarchar(max) NULL,
    [StallNumber] nvarchar(max) NULL,
    [Notes] nvarchar(max) NULL,
    [State] int NOT NULL,
    [RevisionNumber] int NOT NULL,
    [CreatedAtUtc] datetimeoffset NOT NULL,
    [UpdatedAtUtc] datetimeoffset NOT NULL,
    CONSTRAINT [UX_ops_Orders_StableKey] UNIQUE ([StableKey])
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ops_Orders_CollectionDate_State' AND object_id = OBJECT_ID(N'[ops].[Orders]'))
    CREATE INDEX [IX_ops_Orders_CollectionDate_State] ON [ops].[Orders]([CollectionDate],[State]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ops_Orders_CustomerId' AND object_id = OBJECT_ID(N'[ops].[Orders]'))
    CREATE INDEX [IX_ops_Orders_CustomerId] ON [ops].[Orders]([CustomerId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ops_Orders_CollectionSiteId' AND object_id = OBJECT_ID(N'[ops].[Orders]'))
    CREATE INDEX [IX_ops_Orders_CollectionSiteId] ON [ops].[Orders]([CollectionSiteId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ops_Orders_DeliverySiteId' AND object_id = OBJECT_ID(N'[ops].[Orders]'))
    CREATE INDEX [IX_ops_Orders_DeliverySiteId] ON [ops].[Orders]([DeliverySiteId]);
GO

IF OBJECT_ID(N'[ops].[OrderSourceLinks]', N'U') IS NULL
CREATE TABLE [ops].[OrderSourceLinks](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [OrderId] uniqueidentifier NOT NULL,
    [EvidenceId] uniqueidentifier NOT NULL,
    [RevisionNumber] int NOT NULL,
    [LinkedAtUtc] datetimeoffset NOT NULL,
    CONSTRAINT [FK_ops_OrderSourceLinks_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [ops].[Orders]([Id]),
    CONSTRAINT [UX_ops_OrderSourceLinks_Order_Revision] UNIQUE ([OrderId],[RevisionNumber])
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ops_OrderSourceLinks_EvidenceId' AND object_id = OBJECT_ID(N'[ops].[OrderSourceLinks]'))
    CREATE INDEX [IX_ops_OrderSourceLinks_EvidenceId] ON [ops].[OrderSourceLinks]([EvidenceId]);
GO


/* V2 Master Data expansion */
IF COL_LENGTH(N'master.Sites', N'DriverTextName') IS NULL ALTER TABLE [master].[Sites] ADD [DriverTextName] nvarchar(200) NULL;
IF COL_LENGTH(N'master.Sites', N'FullAddress') IS NULL ALTER TABLE [master].[Sites] ADD [FullAddress] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Sites', N'MapLink') IS NULL ALTER TABLE [master].[Sites] ADD [MapLink] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Sites', N'CollectionInstructions') IS NULL ALTER TABLE [master].[Sites] ADD [CollectionInstructions] nvarchar(max) NULL;
GO

IF COL_LENGTH(N'master.Drivers', N'TachoName') IS NULL ALTER TABLE [master].[Drivers] ADD [TachoName] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Drivers', N'Email') IS NULL ALTER TABLE [master].[Drivers] ADD [Email] nvarchar(254) NULL;
IF COL_LENGTH(N'master.Drivers', N'TachoMasterMemberCode') IS NULL ALTER TABLE [master].[Drivers] ADD [TachoMasterMemberCode] nvarchar(80) NULL;
IF COL_LENGTH(N'master.Drivers', N'DriverType') IS NULL ALTER TABLE [master].[Drivers] ADD [DriverType] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Drivers', N'DriverGroup') IS NULL ALTER TABLE [master].[Drivers] ADD [DriverGroup] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Drivers', N'Coding') IS NULL ALTER TABLE [master].[Drivers] ADD [Coding] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Drivers', N'AgencyName') IS NULL ALTER TABLE [master].[Drivers] ADD [AgencyName] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Drivers', N'NorthEligible') IS NULL ALTER TABLE [master].[Drivers] ADD [NorthEligible] bit NULL;
IF COL_LENGTH(N'master.Drivers', N'PreloadEligible') IS NULL ALTER TABLE [master].[Drivers] ADD [PreloadEligible] bit NULL;
IF COL_LENGTH(N'master.Drivers', N'Notes') IS NULL ALTER TABLE [master].[Drivers] ADD [Notes] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Drivers', N'TachoMasterDriverId') IS NULL ALTER TABLE [master].[Drivers] ADD [TachoMasterDriverId] nvarchar(120) NULL;
IF COL_LENGTH(N'master.Drivers', N'LicenceExpiry') IS NULL ALTER TABLE [master].[Drivers] ADD [LicenceExpiry] date NULL;
IF COL_LENGTH(N'master.Drivers', N'LicenceStatus') IS NULL ALTER TABLE [master].[Drivers] ADD [LicenceStatus] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Drivers', N'LastTachoMasterSync') IS NULL ALTER TABLE [master].[Drivers] ADD [LastTachoMasterSync] datetimeoffset NULL;
GO

IF COL_LENGTH(N'master.Vehicles', N'Abbreviation') IS NULL ALTER TABLE [master].[Vehicles] ADD [Abbreviation] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Vehicles', N'Transmission') IS NULL ALTER TABLE [master].[Vehicles] ADD [Transmission] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Vehicles', N'Dvs') IS NULL ALTER TABLE [master].[Vehicles] ADD [Dvs] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Vehicles', N'CabMobile') IS NULL ALTER TABLE [master].[Vehicles] ADD [CabMobile] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Vehicles', N'FuelPin') IS NULL ALTER TABLE [master].[Vehicles] ADD [FuelPin] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Vehicles', N'ShellCard') IS NULL ALTER TABLE [master].[Vehicles] ADD [ShellCard] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Vehicles', N'BpRedCard') IS NULL ALTER TABLE [master].[Vehicles] ADD [BpRedCard] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Vehicles', N'BpPlainCard') IS NULL ALTER TABLE [master].[Vehicles] ADD [BpPlainCard] nvarchar(max) NULL;
IF COL_LENGTH(N'master.Vehicles', N'Notes') IS NULL ALTER TABLE [master].[Vehicles] ADD [Notes] nvarchar(max) NULL;
GO

IF OBJECT_ID(N'[master].[CustomerContacts]', N'U') IS NULL
CREATE TABLE [master].[CustomerContacts](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Code] nvarchar(80) NOT NULL,
    [CustomerId] uniqueidentifier NOT NULL,
    [ContactName] nvarchar(max) NOT NULL,
    [Role] nvarchar(max) NULL,
    [Email] nvarchar(max) NULL,
    [Phone] nvarchar(max) NULL,
    [Notes] nvarchar(max) NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_CustomerContacts_Code] UNIQUE ([Code]),
    CONSTRAINT [FK_master_CustomerContacts_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [master].[Customers]([Id]) ON DELETE CASCADE
);
GO

IF OBJECT_ID(N'[master].[MarketContacts]', N'U') IS NULL
CREATE TABLE [master].[MarketContacts](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Key] nvarchar(300) NOT NULL,
    [MarketName] nvarchar(120) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [StandOrLocation] nvarchar(max) NULL,
    [Salesman] nvarchar(max) NULL,
    [Sender] nvarchar(max) NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_MarketContacts_Key] UNIQUE ([Key])
);
GO

IF OBJECT_ID(N'[master].[SiteCutoffs]', N'U') IS NULL
CREATE TABLE [master].[SiteCutoffs](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Code] nvarchar(80) NOT NULL,
    [SiteId] uniqueidentifier NOT NULL,
    [Plan] nvarchar(max) NULL,
    [StandardCutoff] time NULL,
    [ExtendedCutoff] time NULL,
    [Contact] nvarchar(max) NULL,
    [Notes] nvarchar(max) NULL,
    [Temperature] nvarchar(max) NULL,
    [PalletType] nvarchar(max) NULL,
    [LastDespatchTime] time NULL,
    [PlannedCollectFrom] time NULL,
    [PlannedCollectTo] time NULL,
    [DepotDeliveryDeadline] time NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_SiteCutoffs_Code] UNIQUE ([Code]),
    CONSTRAINT [FK_master_SiteCutoffs_Sites_SiteId] FOREIGN KEY ([SiteId]) REFERENCES [master].[Sites]([Id]) ON DELETE CASCADE
);
GO

IF OBJECT_ID(N'[master].[RouteTimings]', N'U') IS NULL
CREATE TABLE [master].[RouteTimings](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Key] nvarchar(300) NOT NULL,
    [Route] nvarchar(250) NOT NULL,
    [PalletType] nvarchar(max) NULL,
    [LastDespatchTime] time NULL,
    [PlannedCollectFrom] time NULL,
    [PlannedCollectTo] time NULL,
    [DepotDeliveryDeadline] time NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_RouteTimings_Key] UNIQUE ([Key])
);
GO

IF OBJECT_ID(N'[master].[FuelPrices]', N'U') IS NULL
CREATE TABLE [master].[FuelPrices](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Code] nvarchar(80) NOT NULL,
    [WeekCommencing] date NOT NULL,
    [Provider] nvarchar(120) NOT NULL,
    [PricePencePerLitre] decimal(10,4) NOT NULL,
    [IsPricingMaximum] bit NOT NULL,
    [Source] nvarchar(max) NULL,
    [Notes] nvarchar(max) NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_FuelPrices_Code] UNIQUE ([Code])
);
GO

IF OBJECT_ID(N'[master].[SiteAliasCandidates]', N'U') IS NULL
CREATE TABLE [master].[SiteAliasCandidates](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Alias] nvarchar(250) NOT NULL,
    [AliasType] nvarchar(40) NOT NULL,
    [Source] nvarchar(max) NULL,
    [SiteId] uniqueidentifier NULL,
    [Approved] bit NOT NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [FK_master_SiteAliasCandidates_Sites_SiteId] FOREIGN KEY ([SiteId]) REFERENCES [master].[Sites]([Id]) ON DELETE SET NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_SiteAliasCandidates_Type_Alias' AND object_id = OBJECT_ID(N'[master].[SiteAliasCandidates]'))
    CREATE UNIQUE INDEX [UX_master_SiteAliasCandidates_Type_Alias] ON [master].[SiteAliasCandidates]([AliasType],[Alias]);
GO


IF OBJECT_ID(N'[master].[MasterDataReviewItems]', N'U') IS NULL
CREATE TABLE [master].[MasterDataReviewItems](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Key] nvarchar(300) NOT NULL,
    [Category] nvarchar(80) NOT NULL,
    [EntityType] nvarchar(80) NOT NULL,
    [SourceReference] nvarchar(160) NULL,
    [Summary] nvarchar(max) NOT NULL,
    [PayloadJson] nvarchar(max) NULL,
    [Resolved] bit NOT NULL,
    [ResolutionNotes] nvarchar(max) NULL,
    [CreatedAtUtc] datetimeoffset NOT NULL,
    [UpdatedAtUtc] datetimeoffset NOT NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_MasterDataReviewItems_Key] UNIQUE ([Key])
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_master_MasterDataReviewItems_Resolved_Category_CreatedAtUtc' AND object_id = OBJECT_ID(N'[master].[MasterDataReviewItems]'))
    CREATE INDEX [IX_master_MasterDataReviewItems_Resolved_Category_CreatedAtUtc]
    ON [master].[MasterDataReviewItems]([Resolved],[Category],[CreatedAtUtc]);
GO


IF COL_LENGTH(N'master.Trailers', N'Registration') IS NULL ALTER TABLE [master].[Trailers] ADD [Registration] nvarchar(40) NULL;
IF COL_LENGTH(N'master.Trailers', N'EuroPalletCapacity') IS NULL ALTER TABLE [master].[Trailers] ADD [EuroPalletCapacity] int NULL;
IF COL_LENGTH(N'master.Trailers', N'CurrentLocation') IS NULL ALTER TABLE [master].[Trailers] ADD [CurrentLocation] nvarchar(160) NULL;
IF COL_LENGTH(N'master.Trailers', N'MotExpiry') IS NULL ALTER TABLE [master].[Trailers] ADD [MotExpiry] date NULL;
IF COL_LENGTH(N'master.Trailers', N'Notes') IS NULL ALTER TABLE [master].[Trailers] ADD [Notes] nvarchar(max) NULL;
GO


IF COL_LENGTH(N'master.Sites', N'EarliestCollectionTime') IS NULL ALTER TABLE [master].[Sites] ADD [EarliestCollectionTime] time NULL;
IF COL_LENGTH(N'master.Sites', N'LatestCollectionTime') IS NULL ALTER TABLE [master].[Sites] ADD [LatestCollectionTime] time NULL;
IF COL_LENGTH(N'master.Sites', N'EarliestDeliveryTime') IS NULL ALTER TABLE [master].[Sites] ADD [EarliestDeliveryTime] time NULL;
IF COL_LENGTH(N'master.Sites', N'LatestDeliveryTime') IS NULL ALTER TABLE [master].[Sites] ADD [LatestDeliveryTime] time NULL;
IF COL_LENGTH(N'master.Sites', N'StandardCutoff') IS NULL ALTER TABLE [master].[Sites] ADD [StandardCutoff] time NULL;
IF COL_LENGTH(N'master.Sites', N'ExtendedCutoff') IS NULL ALTER TABLE [master].[Sites] ADD [ExtendedCutoff] time NULL;
IF COL_LENGTH(N'master.Sites', N'DeadlineContact') IS NULL ALTER TABLE [master].[Sites] ADD [DeadlineContact] nvarchar(200) NULL;
IF COL_LENGTH(N'master.Sites', N'DeadlineNotes') IS NULL ALTER TABLE [master].[Sites] ADD [DeadlineNotes] nvarchar(max) NULL;
GO
