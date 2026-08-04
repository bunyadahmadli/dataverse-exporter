# Adding a database provider

The export pipeline never touches a database driver directly. Everything goes through four
interfaces defined in `DataverseExporter.Core/Targets`:

| Interface | Responsibility |
|---|---|
| `ITargetProvider` | Stateless factory: opens connections, creates the explorer, names store types. |
| `ITargetConnection` | One open connection: DDL, resume checkpoints, foreign keys, verification queries. |
| `IEntityWriter` | Bulk-writes one entity's pages, each page atomically with its checkpoint. |
| `IDataExplorer` | Read-side for the explorer web app (optional — throw `NotSupportedException` if you skip it). |

The Core side hands you **provider-neutral logical types** (`LogicalType`:
String/Text/Int32/Int64/Boolean/DateTime/Decimal/Double/Guid) wrapped in `ColumnDefinition`
records. Your provider decides what they become in your dialect. Use
`DataverseExporter.SqlServer` as the reference implementation throughout.

## 1. Create the project

```bash
dotnet new classlib -o src/DataverseExporter.Postgres
dotnet sln add src/DataverseExporter.Postgres
```

Reference `DataverseExporter.Core` and your ADO.NET driver (e.g. `Npgsql`).

## 2. Implement the type mapper

Map each `LogicalType` to a store type, e.g. for PostgreSQL:

| LogicalType | PostgreSQL |
|---|---|
| String(n) | `varchar(n)` |
| Text | `text` |
| Int32 / Int64 | `integer` / `bigint` |
| Boolean | `boolean` |
| DateTime | `timestamp` (values are UTC) |
| Decimal(p,s) | `numeric(p,s)` |
| Double | `double precision` |
| Guid | `uuid` |

## 3. Implement `ITargetConnection`

The contracts that actually matter:

- **`Initialize()`** creates the schema and your internal `_MigrationProgress` /
  `_MigrationVerification` tables.
- **`CreateTable`** must be idempotent (no-op when the table exists, unless `recreate`).
- **`EnsureColumns`** adds missing columns and *widens* strings/decimals whose source grew.
  Never narrow, never drop.
- **`CreateWriter` → `IEntityWriter.WritePage`** is the heart of the resume guarantee:
  the page's rows **and** the `EntityProgress` checkpoint must be committed in a *single
  transaction*. Use your fastest bulk path (`COPY` for PostgreSQL, `LOAD DATA` for MySQL).
  Row arrays are aligned with `TableDefinition.Columns`; `null` means database NULL.
- **`CreateForeignKeys`** must not validate existing data (Dataverse data contains dangling
  references by design — SQL Server uses `WITH NOCHECK`, PostgreSQL has `NOT VALID`).
  Prefix constraint names so `DropManagedForeignKeys` can find *only* yours, and keep names
  within your dialect's identifier limit deterministically (hash suffix).
- **Verification helpers** (`CountRows`, `SampleIds`, `FetchRows`, `RowExists`) should avoid
  full scans on big tables when sampling (`TABLESAMPLE` on PostgreSQL too).
- Return database NULLs as CLR `null` from `FetchRows` — Core compares values directly
  against converted Dataverse values.

## 4. Implement `IDataExplorer` (optional but nice)

Whitelist-validate every table/column name against your cached schema, and pass every
filter value as a command parameter. User input must never be concatenated into SQL.

## 5. Register it

In `src/DataverseExporter.Cli/Program.cs` and `src/DataverseExporter.Explorer/Program.cs`:

```csharp
registry.Register("Postgres", s => new PostgresProvider(s));
```

Users select it via configuration:

```json
{ "Target": { "Provider": "Postgres", "ConnectionString": "Host=...;Database=..." } }
```

## 6. Test it

Run a small end-to-end export against a disposable database:

```bash
cd src/DataverseExporter.Cli
dotnet run -- --test                                   # connection smoke test
# then a tiny real export:
# "Export": { "IncludeEntities": ["account", "contact"] }
dotnet run
```

The built-in verification phase is your friend here — it compares the exported rows
field-by-field against Dataverse and will catch type-mapping mistakes (precision loss,
timezone shifts, truncation) on real data.
