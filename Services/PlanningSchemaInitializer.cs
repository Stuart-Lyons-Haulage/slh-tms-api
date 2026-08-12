using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class PlanningSchemaInitializer
{
    public static async Task Apply(TmsDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("""
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NULL
CREATE TABLE dbo.CustomerContacts (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_CustomerContacts PRIMARY KEY,
    CustomerCode nvarchar(40) NOT NULL,
    Name nvarchar(200) NOT NULL,
    Email nvarchar(320) NULL,
    MobileNumber nvarchar(40) NULL,
    ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_CustomerContacts_ReceivesEtaUpdates DEFAULT (1),
    Active bit NOT NULL CONSTRAINT DF_CustomerContacts_Active DEFAULT (1)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerContacts_CustomerCode_Name' AND object_id = OBJECT_ID(N'dbo.CustomerContacts'))
CREATE UNIQUE INDEX IX_CustomerContacts_CustomerCode_Name ON dbo.CustomerContacts(CustomerCode, Name);

IF OBJECT_ID(N'dbo.MarketContacts', N'U') IS NULL
CREATE TABLE dbo.MarketContacts (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_MarketContacts PRIMARY KEY,
    Market nvarchar(80) NOT NULL,
    Name nvarchar(200) NOT NULL,
    StandOrLocation nvarchar(200) NULL,
    Active bit NOT NULL CONSTRAINT DF_MarketContacts_Active DEFAULT (1)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MarketContacts_Market_Name' AND object_id = OBJECT_ID(N'dbo.MarketContacts'))
CREATE UNIQUE INDEX IX_MarketContacts_Market_Name ON dbo.MarketContacts(Market, Name);

IF OBJECT_ID(N'dbo.TransportOrders', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.TransportOrders', N'SellerName') IS NULL ALTER TABLE dbo.TransportOrders ADD SellerName nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'MarketName') IS NULL ALTER TABLE dbo.TransportOrders ADD MarketName nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'StallNumber') IS NULL ALTER TABLE dbo.TransportOrders ADD StallNumber nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'DriverInstructions') IS NULL ALTER TABLE dbo.TransportOrders ADD DriverInstructions nvarchar(1000) NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'MapLink') IS NULL ALTER TABLE dbo.TransportOrders ADD MapLink nvarchar(1000) NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'DeliveryWindowStartUtc') IS NULL ALTER TABLE dbo.TransportOrders ADD DeliveryWindowStartUtc datetimeoffset NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'DeliveryWindowEndUtc') IS NULL ALTER TABLE dbo.TransportOrders ADD DeliveryWindowEndUtc datetimeoffset NULL;
END

IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Drivers', N'MobileNumber') IS NULL
    ALTER TABLE dbo.Drivers ADD MobileNumber nvarchar(40) NULL;

COMMIT TRANSACTION;
""", ct);
    }
}
