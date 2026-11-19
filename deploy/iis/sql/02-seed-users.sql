-- VERITA: sign-in accounts. Run after 01-schema.sql, in the same database.
-- Safe to run again: an account whose email already exists is left as it is.
--
-- Every account starts with the password 123. Change them after the first sign-in: Data Entry > Users
-- (password column), signed in as admin. A password set there must be at least 10 characters.
-- The hash is ASP.NET Identity's PasswordHasher (V3) of "123", the format the API checks at sign-in.
-- Roles and hospital / department scopes are the ones these accounts had before the reset.

SET NOCOUNT ON;

DECLARE @hash nvarchar(max) = N'AQAAAAIAAYagAAAAEIzxS50u3/duUUAwhfK6fUrQJBM0rkd60JA30X4ODMtTW8FVP4svFNTmG8SBLU4h3Q==';

DECLARE @users TABLE (Email nvarchar(256), Name nvarchar(200), Role nvarchar(50), Title nvarchar(200), HospitalScope nvarchar(200), DeptScope nvarchar(200));
INSERT INTO @users (Email, Name, Role, Title, HospitalScope, DeptScope) VALUES
    (N'admin@verita.health',             N'Yousef Al-Otaibi',    N'ADMIN',              N'Platform Administrator',         N'*',       N'*'),
    (N'auditor@verita.health',           N'Sara Al-Juhani',      N'AUDITOR',            N'Internal Audit Lead',            N'*',       N'*'),
    (N'ceo@verita.health',               N'Khalid Al-Rasheed',   N'CEO',                N'Group Chief Executive Officer',  N'*',       N'*'),
    (N'cfo@verita.health',               N'Noura Al-Harbi',      N'CFO',                N'Group Chief Financial Officer',  N'*',       N'*'),
    (N'clinical.director@verita.health', N'Dr. Hana Al-Mutairi', N'CLINICAL_DIRECTOR',  N'Director of Clinical Services',  N'NSH-RYD', N'*'),
    (N'analyst@verita.health',           N'Reem Al-Qahtani',     N'COSTING_ANALYST',    N'Senior Costing Analyst',         N'*',       N'*'),
    (N'costing.manager@verita.health',   N'Faisal Al-Dossary',   N'COSTING_MANAGER',    N'Head of Cost Accounting',        N'*',       N'*'),
    (N'surgery.manager@verita.health',   N'Abdullah Al-Ghamdi',  N'DEPT_MANAGER',       N'Surgery Department Manager',     N'NSH-RYD', N'SURG,CRIT'),
    (N'controller@verita.health',        N'Omar Bin Saleh',      N'FINANCE_CONTROLLER', N'Financial Controller',           N'*',       N'*'),
    (N'rcm@verita.health',               N'Maha Al-Shehri',      N'RCM_MANAGER',        N'Revenue Cycle Manager',          N'*',       N'*'),
    (N'supplychain@verita.health',       N'Turki Al-Anazi',      N'SUPPLY_CHAIN',       N'Supply Chain Manager',           N'*',       N'*');

INSERT INTO dbo.[User] (Id, Email, Name, PasswordHash, Role, HospitalScope, DeptScope, Title, IsActive, LastLoginAt, CreatedAt)
SELECT NEWID(), u.Email, u.Name, @hash, u.Role, u.HospitalScope, u.DeptScope, u.Title, 1, NULL, SYSUTCDATETIME()
FROM @users u
WHERE NOT EXISTS (SELECT 1 FROM dbo.[User] x WHERE x.Email = u.Email);

PRINT CONCAT(@@ROWCOUNT, N' account(s) added.');
SELECT Email, Role, HospitalScope, DeptScope, IsActive FROM dbo.[User] ORDER BY Role, Email;
