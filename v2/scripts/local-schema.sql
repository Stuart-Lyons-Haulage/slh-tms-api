SET XACT_ABORT ON;
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
    [Latitude] decimal(9,6) NULL,
    [Longitude] decimal(9,6) NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_Sites_Code] UNIQUE ([Code]),
    CONSTRAINT [FK_master_Sites_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [master].[Customers]([Id])
);
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
    [TrailerType] nvarchar(max) NULL,
    [PalletCapacity] int NULL,
    [Active] bit NOT NULL,
    CONSTRAINT [UX_master_Trailers_TrailerNumber] UNIQUE ([TrailerNumber])
);
GO

IF OBJECT_ID(N'[master].[ExternalIdentities]', N'U') IS NULL
CREATE TABLE [master].[ExternalIdentities](
    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
    [Provider] nvarchar(450) NOT NULL,
    [EntityType] nvarchar(450) NOT NULL,
    [EntityId] uniqueidentifier NOT NULL,
    [ExternalKey] nvarchar(450) NOT NULL,
    [ExternalDisplayName] nvarchar(max) NULL,
    [Active] bit NOT NULL
);
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
CREATE INDEX [IX_ops_Orders_CollectionDate_State] ON [ops].[Orders]([CollectionDate],[State]);
CREATE INDEX [IX_ops_Orders_CustomerId] ON [ops].[Orders]([CustomerId]);
CREATE INDEX [IX_ops_Orders_CollectionSiteId] ON [ops].[Orders]([CollectionSiteId]);
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
CREATE INDEX [IX_ops_OrderSourceLinks_EvidenceId] ON [ops].[OrderSourceLinks]([EvidenceId]);
GO
