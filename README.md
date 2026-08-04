# Dataverse Exporter

Mirror every table and column of a Dynamics 365 CRM (Dataverse) environment into your own
relational database, then browse the result in a lightweight web UI.

- **Full export** — schema + data for all entities (or a chosen subset), with real foreign keys.
- **Resumable** — every page is committed together with a checkpoint; interrupt the export at
  any moment (Ctrl+C, network drop, crash) and it continues exactly where it left off, with no
  duplicate or missing rows.
- **Verified** — after the export, row counts and random sample records are compared
  field-by-field against Dataverse.
- **Pluggable targets** — the pipeline is database-agnostic. SQL Server ships in the box;
  adding PostgreSQL/MySQL/SQLite is a matter of implementing one provider interface
  (see [docs/adding-a-provider.md](docs/adding-a-provider.md)).
- **Code generation** — EF Core compatible POCO classes + a `DbContext` for every exported
  entity, ready to drop into your own API project.
- **Data explorer** — a small web app to search tables, filter/sort columns and download CSVs.

## Projects

| Project | What it is |
|---|---|
| `src/DataverseExporter.Core` | Dataverse metadata/data reading, orchestration, verification, model generation. Knows nothing about any specific database. |
| `src/DataverseExporter.SqlServer` | The SQL Server target provider (DDL, `SqlBulkCopy`, foreign keys, explorer queries). |
| `src/DataverseExporter.Cli` | The console app that runs the export. |
| `src/DataverseExporter.Explorer` | The data explorer web app (`http://localhost:5180`). |

## How it works

1. `RetrieveAllEntitiesRequest` pulls all entity metadata (table + column definitions).
2. Each entity's attributes are mapped to **provider-neutral logical types**; the selected
   provider turns them into store types and emits `CREATE TABLE`.
3. Each entity's data is fetched with `QueryExpression` + paging cookies in pages of 5,000
   and bulk-inserted into the target table.

### Type mapping

| Dataverse | Logical type | SQL Server |
|---|---|---|
| String / Memo | String(n) / Text | NVARCHAR(n) / NVARCHAR(MAX) |
| Integer / BigInt | Int32 / Int64 | INT / BIGINT |
| Boolean | Boolean | BIT |
| DateTime | DateTime (UTC) | DATETIME2 |
| Decimal | Decimal(28, 12) | DECIMAL(28, 12) |
| Double | Double | FLOAT |
| Money | Decimal(28, 10) | DECIMAL(28, 10) |
| Lookup / Customer / Owner / Uniqueidentifier | Guid (the record's id) | UNIQUEIDENTIFIER |
| Picklist / State / Status | Int32 (option set value) | INT |
| MultiSelectPicklist | Text (`1;2;3`) | NVARCHAR(MAX) |
| PartyList, File, Image, virtual fields | not exported | — |

Lookup display names and option set labels are not exported; if you need them, the
`stringmap` entity contains the option set labels and is exported like any other table.

### Relationships (foreign keys)

- **Single-target lookups** get a real `FOREIGN KEY` constraint on the target table.
  Constraints are added `WITH NOCHECK`: records whose target was deleted in CRM don't break
  the export, but the relationship is visible in the schema and usable in joins.
- **Multi-target lookups** (`customerid` → account/contact, `ownerid` → systemuser/team,
  `regardingobjectid` → dozens of entities) cannot reference a single table. They get a
  companion `<column>_entitytype` column holding the logical name of the referenced table.
- FKs are prefixed `FK_crm_`. They are dropped automatically before the data phase
  (`TRUNCATE` fails on referenced tables) and re-created afterwards — only between tables
  whose export **completed**.

### Generated C# models

With `Models:Generate` enabled (default), every run writes to `Models:OutputDirectory`:

- one EF Core compatible POCO per entity, with `[Table]`, `[Column]`, `[Key]` attributes and
  entity/field display names as XML doc comments;
- a `CrmDbContext` that collects them all.

Copy the folder into your API project, add `Microsoft.EntityFrameworkCore.SqlServer` and:

```csharp
services.AddDbContext<CrmDbContext>(o => o.UseSqlServer(connectionString));
```

## Setup

### 1. Azure AD app registration (for ClientSecret auth)

1. Azure Portal → App registrations → New registration.
2. Certificates & secrets → create a client secret.
3. Power Platform Admin Center → your environment → Settings → Users + permissions →
   Application users → add this app as an **application user** with the
   **System Administrator** role (or at least read access to all tables).

### 2. Configuration

Fill in `src/DataverseExporter.Cli/appsettings.json` (or `appsettings.local.json`, which is
git-ignored):

```json
{
  "Dataverse": {
    "ConnectionString": "AuthType=ClientSecret; Url=https://yourorg.crm.dynamics.com; ClientId=...; ClientSecret=..."
  },
  "Target": {
    "Provider": "SqlServer",
    "ConnectionString": "Server=...;Database=CrmMirror;User Id=...;Password=...;TrustServerCertificate=True;",
    "Schema": "dbo"
  }
}
```

All settings can also come from environment variables with the `DVEXPORT_` prefix and `__`
as the section separator (e.g. `DVEXPORT_Target__ConnectionString`).

| Setting | Description |
|---|---|
| `Target:Provider` | Target database provider (`SqlServer`; more can be registered) |
| `Target:Schema` | Target schema (default `dbo`) |
| `Export:IncludeEntities` | When non-empty, only these entities are exported (logical names) |
| `Export:ExcludeEntities` | Entities to skip |
| `Export:IncludeIntersectTables` | Export N:N relationship tables (default `true`) |
| `Export:RecreateTables` | `true` → DROP and recreate tables (resets progress too) |
| `Export:TruncateBeforeInsert` | `true` → TRUNCATE tables that start from scratch (default `true`) |
| `Export:ResetProgress` | `true` → clear `_MigrationProgress` checkpoints and start over |
| `Export:SchemaOnly` | `true` → create tables only, copy no data |
| `Export:PageSize` | Records per page (max 5000) |
| `Export:CreateForeignKeys` | `true` → create FK constraints for single-target lookups (default `true`) |
| `Export:MaxDegreeOfParallelism` | Entities exported concurrently; `0` = value recommended by Dataverse (default `0`) |
| `Models:Generate` | `true` → generate EF Core compatible POCO classes (default `true`) |
| `Models:OutputDirectory` | Where the generated classes are written (default `./generated-models`) |
| `Models:Namespace` | Namespace of the generated classes (default `Crm.Models`) |
| `Verification:Enabled` | `true` → run the verification phase after the export (default `true`) |
| `Verification:SampleSize` | Sample records per entity compared field-by-field (default `10`) |

### 3. Run

```bash
cd src/DataverseExporter.Cli
dotnet run -c Release            # full export
dotnet run -c Release -- --test  # only test both connections, then exit
```

System entities that cannot be queried don't fail the run; they are skipped and listed at
the end.

## Resume

Progress is kept in the `_MigrationProgress` table of the target database:

- Every 5,000-row page is committed **in the same transaction** as its page number and
  Dataverse paging cookie. Wherever the run is interrupted, no duplicate or missing rows
  can occur.
- Restart the app and nothing else is needed: completed entities are skipped, a
  half-finished entity continues from its last page, untouched ones start fresh.
- To re-export everything, set `"Export": { "ResetProgress": true }` (or empty the
  `_MigrationProgress` table).

Note: resume relies on deterministic ordering (primary key). If the export runs for days
while CRM receives heavy writes, a few records added mid-run may be missed in that table;
re-export critical tables afterwards with `IncludeEntities` + `ResetProgress`.

## Performance (large environments)

- Entities are exported **in parallel**: each worker uses its own Dataverse client clone
  (token shared) and its own database connection. Parallelism can be pinned with
  `Export:MaxDegreeOfParallelism`; `0` uses the server's `x-ms-dop-hint` recommendation.
- `EnableAffinityCookie = false` spreads requests across Dataverse web servers
  (Microsoft's recommendation for high-volume reads).
- All entity record counts are fetched up front and the **biggest tables enter the queue
  first**, so parallel workers don't go idle near the end.
- Pages are written as they arrive via bulk copy (table lock); throughput-oriented Server GC
  is enabled.
- When Dataverse throttles, the client waits and retries automatically.

## Verification

With `Verification:Enabled` (default), four checks run for every completed entity, and the
results go to the console and the `_MigrationVerification` table:

1. **Exact row count** — the target's `COUNT(*)` must equal the number of rows read from
   Dataverse during the export. Mismatch → **Error**.
2. **Cross-check with the CRM counter** — Dataverse's total record counter
   (`RetrieveTotalRecordCount`) is compared with a 1% / 100-row tolerance; beyond it →
   **Warning** (the counter is a snapshot that can lag up to 24 hours).
3. **Sample comparison** — `Verification:SampleSize` random records per entity are
   re-fetched from CRM and **all columns** are compared with the target. Differences →
   **Error**; records legitimately updated/deleted after the export (different `modifiedon`)
   are reported separately as **Warning**. On big tables the sampling avoids full scans.
4. **Tail check** — the newest CRM record (`createdon` desc) is looked up in the target;
   if missing, records created mid-export may have been skipped → **Warning**.

Exit codes: `0` = all good, `2` = some entities could not be exported (listed),
`3` = verification errors exist.

If you see a verification error, the typical fix is to put the affected entities in
`IncludeEntities` and re-run with `ResetProgress: true`.

## Data explorer

Browse and filter the exported tables in a browser:

```bash
cd src/DataverseExporter.Explorer
dotnet run        # http://localhost:5180
```

- **UI**: table search/selection on the left (only tables with data, with row counts),
  per-column filter chain (contains, equals, >, empty/not empty...), click-to-sort headers,
  column picker, paging, filtered CSV download.
- **API endpoints** (dynamic — driven by database metadata instead of compiling thousands of
  classes; table/column names are validated against a whitelist, values are always sent as
  parameters):
  - `GET /api/tables` — tables with data + row counts
  - `GET /api/tables/{table}/columns` — columns, types, PK
  - `GET /api/tables/{table}/data?page=&pageSize=&sort=&dir=&columns=&f=column|op|value` —
    op: `eq, ne, gt, gte, lt, lte, contains, startswith, null, notnull`
    (`f` is repeatable, combined with AND)
  - `GET /api/tables/{table}/export` — CSV with the same parameters (up to 100k rows)
  - `POST /api/refresh` — refresh the schema/row-count cache
- The connection string lives in `appsettings.local.json` (git-ignored), same `Target`
  section as the CLI.

## Adding a database provider

The exporter talks to the target database exclusively through the `ITargetProvider` /
`ITargetConnection` / `IEntityWriter` / `IDataExplorer` interfaces in
`DataverseExporter.Core`. To support PostgreSQL, MySQL, SQLite... implement those interfaces
in a new project and register it in one line. The full walkthrough is in
[docs/adding-a-provider.md](docs/adding-a-provider.md).

## Notes

- Start with a small trial: `"Export": { "SchemaOnly": true, "IncludeEntities": ["account", "contact"] }`.
- All dates are written in **UTC**, exactly as Dataverse stores them.
- Dataverse enforces API request limits; very large environments can take a long time, the
  client backs off and retries automatically when throttled.

## License

[MIT](LICENSE)
