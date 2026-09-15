IF COL_LENGTH(N'dbo.DistributedLease', N'RunId') IS NULL
BEGIN
    ALTER TABLE dbo.DistributedLease ADD RunId nvarchar(64) NULL;
    UPDATE dbo.DistributedLease SET RunId = CONVERT(nvarchar(64), NEWID()) WHERE RunId IS NULL;
    ALTER TABLE dbo.DistributedLease ALTER COLUMN RunId nvarchar(64) NOT NULL;
END;

IF COL_LENGTH(N'dbo.DistributedLease', N'HeartbeatAt') IS NULL
BEGIN
    ALTER TABLE dbo.DistributedLease ADD HeartbeatAt datetime2(7) NULL;
    UPDATE dbo.DistributedLease SET HeartbeatAt = AcquiredAt WHERE HeartbeatAt IS NULL;
    ALTER TABLE dbo.DistributedLease ALTER COLUMN HeartbeatAt datetime2(7) NOT NULL;
END;
