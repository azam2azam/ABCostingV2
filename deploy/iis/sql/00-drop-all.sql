-- VERITA: EMPTY THE DATABASE. Drops every user object: tables (with their temporal history tables),
-- views, functions, procedures, synonyms, sequences and user-defined types, in every schema except sys.
-- The database itself, its users/logins and its schemas are kept. THIS CANNOT BE UNDONE: back up first.
--
-- Then run, in the same database: 01-schema.sql, 02-seed-users.sql, 03-seed-hospitals.sql.
--
-- Safety: set @confirm below to the name of the database you mean to empty. Any other value stops the
-- script before anything is dropped.
--   SSMS:   select the database, edit @confirm, Execute.
--   sqlcmd: sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -i 00-drop-all.sql
--           (edit @confirm in the file first)
-- Works inside an existing database: it never creates or drops the database, so no server-level rights are
-- needed. Needs SQL Server 2016 or newer (temporal tables) and rights to drop objects in it (db_owner, or
-- db_ddladmin). The same login can then run 01-schema.sql.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @confirm sysname = N'';   -- <<< type the database name here, e.g. N'Verita'

DECLARE @db sysname = DB_NAME();

IF @confirm IS NULL OR @confirm <> @db
BEGIN
    RAISERROR (N'Nothing was dropped. Set @confirm to this database''s name (%s) to empty it.', 16, 1, @db) WITH NOWAIT;
    SET NOEXEC ON;   -- SSMS keeps going after an error; this skips every following batch
END;
GO

PRINT CONCAT(N'Emptying database ', QUOTENAME(DB_NAME()), N' ...');

-- Runs each statement of a list; with @tolerant = 1 a failure is kept for a later step instead of stopping.
DECLARE @sql nvarchar(max), @failed int = 0;
DECLARE @steps TABLE (Seq int IDENTITY, Stmt nvarchar(max));

-- 1. Check and default constraints. Some call schema-bound functions, and a schema-bound function stops its
--    tables being altered (turning system versioning off below).
INSERT INTO @steps (Stmt)
SELECT N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id))
     + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
FROM sys.objects WHERE type IN ('C', 'D') AND is_ms_shipped = 0;

-- 2. Views (some schema-bound to tables), procedures, synonyms, then functions.
INSERT INTO @steps (Stmt)
SELECT CASE o.type WHEN 'V' THEN N'DROP VIEW ' WHEN 'P' THEN N'DROP PROCEDURE ' WHEN 'SN' THEN N'DROP SYNONYM ' ELSE N'DROP FUNCTION ' END
     + QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name) + N';'
FROM sys.objects o
WHERE o.is_ms_shipped = 0 AND o.type IN ('V', 'P', 'SN', 'FN', 'IF', 'TF', 'FS', 'FT')
ORDER BY CASE o.type WHEN 'V' THEN 0 WHEN 'P' THEN 1 WHEN 'SN' THEN 2 ELSE 3 END;

DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Stmt FROM @steps ORDER BY Seq;
OPEN c; FETCH NEXT FROM c INTO @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY EXEC sys.sp_executesql @sql; END TRY
    BEGIN CATCH PRINT CONCAT(N'  later: ', @sql, N' (', ERROR_MESSAGE(), N')'); END CATCH;   -- e.g. a function a computed column uses
    FETCH NEXT FROM c INTO @sql;
END;
CLOSE c; DEALLOCATE c;
DELETE FROM @steps;

-- 3. Temporal tables: turn system versioning off, so each table and its history table can be dropped.
INSERT INTO @steps (Stmt)
SELECT N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' SET (SYSTEM_VERSIONING = OFF);'
FROM sys.tables WHERE temporal_type = 2;

-- 4. Foreign keys, so tables can go in any order.
INSERT INTO @steps (Stmt)
SELECT N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id))
     + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
FROM sys.foreign_keys;

-- 5. Tables, including history tables and __EFMigrationsHistory.
INSERT INTO @steps (Stmt)
SELECT N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
FROM sys.tables WHERE is_ms_shipped = 0;

DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Stmt FROM @steps ORDER BY Seq;
OPEN c; FETCH NEXT FROM c INTO @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC sys.sp_executesql @sql;
    FETCH NEXT FROM c INTO @sql;
END;
CLOSE c; DEALLOCATE c;
DELETE FROM @steps;

-- 6. Whatever is left: functions that tables were still using, sequences, user-defined types.
INSERT INTO @steps (Stmt)
SELECT CASE o.type WHEN 'SO' THEN N'DROP SEQUENCE ' WHEN 'V' THEN N'DROP VIEW ' WHEN 'P' THEN N'DROP PROCEDURE ' WHEN 'SN' THEN N'DROP SYNONYM ' ELSE N'DROP FUNCTION ' END
     + QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name) + N';'
FROM sys.objects o
WHERE o.is_ms_shipped = 0 AND o.type IN ('V', 'P', 'SN', 'FN', 'IF', 'TF', 'FS', 'FT', 'SO');
INSERT INTO @steps (Stmt)
SELECT N'DROP TYPE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
FROM sys.types WHERE is_user_defined = 1;

DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Stmt FROM @steps ORDER BY Seq;
OPEN c; FETCH NEXT FROM c INTO @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC sys.sp_executesql @sql;
    FETCH NEXT FROM c INTO @sql;
END;
CLOSE c; DEALLOCATE c;

DECLARE @left int = (SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'SN', 'SO'))
                  + (SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1);
IF @left > 0
    RAISERROR (N'%d object(s) could not be dropped; see the messages above.', 16, 1, @left);
ELSE
    PRINT N'Done: the database is empty. Next: 01-schema.sql.';
GO

SET NOEXEC OFF;
GO
