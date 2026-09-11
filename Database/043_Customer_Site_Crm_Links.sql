IF COL_LENGTH(N'dbo.Customers', N'TradingName') IS NULL
    ALTER TABLE dbo.Customers ADD TradingName nvarchar(200) NULL;
IF COL_LENGTH(N'dbo.Customers', N'AccountOwner') IS NULL
    ALTER TABLE dbo.Customers ADD AccountOwner nvarchar(200) NULL;
IF COL_LENGTH(N'dbo.Customers', N'ServiceNotes') IS NULL
    ALTER TABLE dbo.Customers ADD ServiceNotes nvarchar(1000) NULL;
IF COL_LENGTH(N'dbo.Customers', N'DefaultSiteCode') IS NULL
    ALTER TABLE dbo.Customers ADD DefaultSiteCode nvarchar(80) NULL;
IF COL_LENGTH(N'dbo.Sites', N'CustomerCode') IS NULL
    ALTER TABLE dbo.Sites ADD CustomerCode nvarchar(40) NULL;

IF OBJECT_ID(N'dbo.CustomerEmailRoutes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerEmailRoutes
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CustomerEmailRoutes PRIMARY KEY,
        CustomerCode nvarchar(40) NOT NULL,
        SenderEmail nvarchar(320) NULL,
        SenderDomain nvarchar(320) NULL,
        SubjectContains nvarchar(200) NULL,
        ParserType nvarchar(120) NULL,
        DefaultSiteCode nvarchar(80) NULL,
        RequiresReview bit NOT NULL CONSTRAINT DF_CustomerEmailRoutes_RequiresReview DEFAULT(1),
        Active bit NOT NULL CONSTRAINT DF_CustomerEmailRoutes_Active DEFAULT(1)
    );
END;

-- These lookup indexes are deliberately deferred from the blocking startup
-- migration. The customer/site register is a live production dataset and an
-- online index build can keep the API from opening its port long enough for
-- Container Apps to terminate the revision. The CRM queries remain correct
-- without them; add them later through the online-maintenance process.
