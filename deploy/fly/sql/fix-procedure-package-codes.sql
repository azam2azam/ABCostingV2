-- One-off data fix: procedure and package codes lose their hospital prefix (SWD-AGL.01002003 -> 01002003).
-- A code is now unique within its hospital, so the old group-wide unique index is replaced by a per-hospital one;
-- that is the only schema change, and without it the stripped codes would collide across hospitals.
-- Safe to run twice: it only touches codes that still carry the prefix. Stops without changing anything if two rows
-- in the same hospital would end up with the same code.
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRAN;

IF EXISTS (SELECT 1 FROM [Procedure] p JOIN Hospital h ON h.Id = p.HospitalId
           WHERE LEFT(p.Code, LEN(h.Code) + 1) = h.Code + '.'
           GROUP BY p.HospitalId, SUBSTRING(p.Code, LEN(h.Code) + 2, LEN(p.Code)) HAVING COUNT(*) > 1)
    THROW 50001, 'Two procedures in one hospital would share a code once the prefix is removed. Nothing was changed.', 1;
IF EXISTS (SELECT 1 FROM Package p JOIN Hospital h ON h.Id = p.HospitalId
           WHERE LEFT(p.Code, LEN(h.Code) + 1) = h.Code + '.'
           GROUP BY p.HospitalId, SUBSTRING(p.Code, LEN(h.Code) + 2, LEN(p.Code)) HAVING COUNT(*) > 1)
    THROW 50002, 'Two packages in one hospital would share a code once the prefix is removed. Nothing was changed.', 1;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Procedure_Code' AND object_id = OBJECT_ID('[Procedure]')) DROP INDEX IX_Procedure_Code ON [Procedure];
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Package_Code' AND object_id = OBJECT_ID('Package')) DROP INDEX IX_Package_Code ON Package;

UPDATE p SET p.Code = SUBSTRING(p.Code, LEN(h.Code) + 2, LEN(p.Code))
FROM [Procedure] p JOIN Hospital h ON h.Id = p.HospitalId
WHERE LEFT(p.Code, LEN(h.Code) + 1) = h.Code + '.' AND LEN(p.Code) > LEN(h.Code) + 1;
PRINT CONCAT('Procedures updated: ', @@ROWCOUNT);
UPDATE p SET p.Code = SUBSTRING(p.Code, LEN(h.Code) + 2, LEN(p.Code))
FROM Package p JOIN Hospital h ON h.Id = p.HospitalId
WHERE LEFT(p.Code, LEN(h.Code) + 1) = h.Code + '.' AND LEN(p.Code) > LEN(h.Code) + 1;
PRINT CONCAT('Packages updated: ', @@ROWCOUNT);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Procedure_HospitalId_Code' AND object_id = OBJECT_ID('[Procedure]'))
    CREATE UNIQUE INDEX IX_Procedure_HospitalId_Code ON [Procedure] (HospitalId, Code);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Package_HospitalId_Code' AND object_id = OBJECT_ID('Package'))
    CREATE UNIQUE INDEX IX_Package_HospitalId_Code ON Package (HospitalId, Code);
-- The composite index starts with HospitalId, so the separate HospitalId indexes are redundant.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Procedure_HospitalId' AND object_id = OBJECT_ID('[Procedure]')) DROP INDEX IX_Procedure_HospitalId ON [Procedure];
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Package_HospitalId' AND object_id = OBJECT_ID('Package')) DROP INDEX IX_Package_HospitalId ON Package;

-- Record it as the EF migration of the same name, so a later `dotnet ef database update` does not try to apply it again.
IF OBJECT_ID('__EFMigrationsHistory') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM __EFMigrationsHistory WHERE MigrationId = '20261111082847_HospitalLocalProcedureCodes')
    INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20261111082847_HospitalLocalProcedureCodes', '8.0.11');

COMMIT;
