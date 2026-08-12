IF COL_LENGTH(N'dbo.MarketContacts', N'Sender') IS NULL
ALTER TABLE dbo.MarketContacts ADD Sender nvarchar(200) NULL;
