/* Commercial control fields used by margin and seven-day forecasting. */
SET XACT_ABORT OFF;
DECLARE @commercialChanges table (SqlText nvarchar(max) NOT NULL);
INSERT @commercialChanges VALUES
(N'IF COL_LENGTH(N''dbo.Loads'', N''RevenueAmount'') IS NULL ALTER TABLE dbo.Loads ADD RevenueAmount decimal(12,2) NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''FuelSurchargeAmount'') IS NULL ALTER TABLE dbo.Loads ADD FuelSurchargeAmount decimal(12,2) NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''EstimatedCostAmount'') IS NULL ALTER TABLE dbo.Loads ADD EstimatedCostAmount decimal(12,2) NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''ActualCostAmount'') IS NULL ALTER TABLE dbo.Loads ADD ActualCostAmount decimal(12,2) NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''EstimatedDistanceMiles'') IS NULL ALTER TABLE dbo.Loads ADD EstimatedDistanceMiles decimal(10,1) NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''EmptyMiles'') IS NULL ALTER TABLE dbo.Loads ADD EmptyMiles decimal(10,1) NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''InvoiceStatus'') IS NULL ALTER TABLE dbo.Loads ADD InvoiceStatus nvarchar(40) NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''CommercialNotes'') IS NULL ALTER TABLE dbo.Loads ADD CommercialNotes nvarchar(500) NULL');

DECLARE @commercialSql nvarchar(max);
DECLARE commercial_repair_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT SqlText FROM @commercialChanges;
OPEN commercial_repair_cursor; FETCH NEXT FROM commercial_repair_cursor INTO @commercialSql;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY EXEC sys.sp_executesql @commercialSql; END TRY BEGIN CATCH PRINT ERROR_MESSAGE(); END CATCH;
    FETCH NEXT FROM commercial_repair_cursor INTO @commercialSql;
END;
CLOSE commercial_repair_cursor; DEALLOCATE commercial_repair_cursor;
