-- VERITA: sign-in accounts for a new database. Run after 01-schema.sql, in the same database.
-- Safe to run again: an account whose email already exists is left as it is.
--
-- Every account starts with the demo password Demo!Pass123 (the one in README.md).
-- Change them after the first sign-in: Data Entry > Users (password column), signed in as admin.
-- The hash is ASP.NET Identity's PasswordHasher (V3) of that password, as FixtureImporter writes it.

SET NOCOUNT ON;

DECLARE @hash nvarchar(max) = N'AQAAAAIAAYagAAAAENy01ibZImJtmTmrBmhrBeeirasS1vee0oYiBhWXe5kbwjIllFkaaMWKivm3QEt+pw==';

DECLARE @users TABLE (Email nvarchar(256), Name nvarchar(200), Role nvarchar(50), Title nvarchar(200));
INSERT INTO @users (Email, Name, Role, Title) VALUES
    (N'admin@verita.health',           N'Yousef Al-Otaibi',  N'ADMIN',              N'Platform Administrator'),
    (N'cfo@verita.health',             N'Noura Al-Harbi',    N'CFO',                N'Group Chief Financial Officer'),
    (N'ceo@verita.health',             N'Khalid Al-Rasheed', N'CEO',                N'Group Chief Executive Officer'),
    (N'costing.manager@verita.health', N'Faisal Al-Dossary', N'COSTING_MANAGER',    N'Head of Cost Accounting'),
    (N'analyst@verita.health',         N'Reem Al-Qahtani',   N'COSTING_ANALYST',    N'Senior Costing Analyst'),
    (N'controller@verita.health',      N'Omar Bin Saleh',    N'FINANCE_CONTROLLER', N'Financial Controller'),
    (N'rcm@verita.health',             N'Maha Al-Shehri',    N'RCM_MANAGER',        N'Revenue Cycle Manager'),
    (N'supplychain@verita.health',     N'Turki Al-Anazi',    N'SUPPLY_CHAIN',       N'Supply Chain Manager'),
    (N'auditor@verita.health',         N'Sara Al-Juhani',    N'AUDITOR',            N'Internal Audit Lead');

INSERT INTO dbo.[User] (Id, Email, Name, PasswordHash, Role, HospitalScope, DeptScope, Title, IsActive, LastLoginAt, CreatedAt)
SELECT NEWID(), u.Email, u.Name, @hash, u.Role, N'*', N'*', u.Title, 1, NULL, SYSUTCDATETIME()
FROM @users u
WHERE NOT EXISTS (SELECT 1 FROM dbo.[User] x WHERE x.Email = u.Email);

PRINT CONCAT(@@ROWCOUNT, N' account(s) added.');
SELECT Email, Role, IsActive FROM dbo.[User] ORDER BY Role;
