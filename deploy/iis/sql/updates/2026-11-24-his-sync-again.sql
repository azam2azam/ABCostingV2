-- HIS Sync "Sync again": a range's mode, and per synced day the fact parts it holds and the parts a run reads.
-- Safe to run twice.
IF COL_LENGTH(N'HisSyncRange', N'Mode') IS NULL
    ALTER TABLE [HisSyncRange] ADD [Mode] nvarchar(16) NOT NULL CONSTRAINT [DF_HisSyncRange_Mode] DEFAULT N'SYNC';
GO
IF COL_LENGTH(N'HisSyncDay', N'Sections') IS NULL
    ALTER TABLE [HisSyncDay] ADD [Sections] nvarchar(400) NULL;
GO
IF COL_LENGTH(N'HisSyncDay', N'Needs') IS NULL
    ALTER TABLE [HisSyncDay] ADD [Needs] nvarchar(400) NULL;
GO
