-- VERITA: the two hospitals, as they were before the reset (same ids and codes). Run after 01-schema.sql.
-- Safe to run again: a hospital whose code already exists is left as it is.
--
-- SWD-AGL keeps its HIS link (SetupID 010266 / ProjectID 17), so HIS packs and the HIS sync worker load into it
-- straight away. NSH-RYD is not linked; it is linked on its first HIS import.
-- Everything else (catalogue, facts, periods, costing models) is loaded afterwards: Data Import or HIS Sync.

SET NOCOUNT ON;

DECLARE @hospitals TABLE (Id uniqueidentifier, Code nvarchar(200), Name nvarchar(200), City nvarchar(200), Currency nvarchar(200),
    BedCount int, IsActive bit, CreatedAt datetime2, HisSetupId nvarchar(200), HisProjectId int);
INSERT INTO @hospitals (Id, Code, Name, City, Currency, BedCount, IsActive, CreatedAt, HisSetupId, HisProjectId) VALUES
    ('CC8058A9-78B9-4EED-8ED6-39AAF4629D69', N'SWD-AGL', N'Swedi Hospital 2',                       N'Riyadh', N'SAR', 330, 1, '2026-11-03T13:04:11.723', N'010266', 17),
    ('F5453950-9882-1135-A11F-CBD1E889FDFC', N'NSH-RYD', N'Northgate Specialist Hospital — Riyadh', N'Riyadh', N'SAR',  96, 1, '2026-09-22T02:00:30.938', NULL,      NULL);

INSERT INTO dbo.Hospital (Id, Code, Name, City, Currency, BedCount, IsActive, CreatedAt, HisSetupId, HisProjectId)
SELECT h.Id, h.Code, h.Name, h.City, h.Currency, h.BedCount, h.IsActive, h.CreatedAt, h.HisSetupId, h.HisProjectId
FROM @hospitals h
WHERE NOT EXISTS (SELECT 1 FROM dbo.Hospital x WHERE x.Code = h.Code OR x.Id = h.Id);

PRINT CONCAT(@@ROWCOUNT, N' hospital(s) added.');
SELECT Code, Name, HisSetupId, HisProjectId, IsActive FROM dbo.Hospital ORDER BY Code;
