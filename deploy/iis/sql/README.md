# VERITA database scripts

The VERITA schema is defined here, and only here. The API and the HIS Sync worker never create or change
tables, and the code has no EF Core migrations. Every script runs inside an existing database: none creates or
drops a database, so database-level rights (`db_owner`, or `db_ddladmin` with read/write) are enough.

## Starting a database from scratch

Run, in this order, against the same database (SSMS, or `sqlcmd -S <server> -d <database> -U <user> -P <password> -C -b -i <file>`):

| Script | What it does |
|---|---|
| `00-drop-all.sql` | Empties the database: tables (and temporal history tables), views, functions, procedures, synonyms, sequences, user-defined types. Keeps the database, its users and its schemas. **Set `@confirm` at the top to the database name first**; otherwise nothing is dropped. Cannot be undone: back up first. Skip it for a new, empty database. |
| `01-schema.sql` | Creates every table, view, function and constraint. Refuses to run if the database already has tables. |
| `02-seed-users.sql` | The sign-in accounts, all with password `123`. Change them after the first sign-in. |
| `03-seed-hospitals.sql` | The two hospitals, SWD-AGL (linked to HIS 010266 / 17) and NSH-RYD. |

`02` and `03` can be run again; rows that already exist are skipped. After that, load the catalogue and facts
with Data Import or HIS Sync.

For local development, `verita-tools migrate --reset` runs `00` and `01` against `ConnectionStrings__Verita`
(it also creates the database when it does not exist, which needs server rights).

## Changing the schema

1. Change the entity and `VeritaDbContext` as usual.
2. Write the change as a script in `updates/`, named by date and purpose, e.g. `updates/2026-12-01-add-x.sql`.
   Make it safe to run twice (`IF COL_LENGTH(...) IS NULL`, `IF OBJECT_ID(...) IS NULL`). It is what existing
   databases run.
3. Make the same change in `01-schema.sql`, so a new database comes out the same as an updated one.
4. For the exact DDL the model expects (column types, lengths, index names), print the model's script and copy
   the relevant part: `dotnet ef dbcontext script -p src/Verita.Infrastructure -s src/Verita.Api -o /tmp/model.sql`.
   It does not contain the hand-written views, functions and check constraints, which live only in `01-schema.sql`.
5. Deploy: run the update script, then deploy the API and HIS Sync.
