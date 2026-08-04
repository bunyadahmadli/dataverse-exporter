# Contributing

Thanks for your interest! A few ground rules to keep things smooth:

- **Bugs / feature requests** — open an issue with reproduction steps (Dataverse entity,
  setting combination, error output). Never include connection strings or secrets.
- **New database providers** — the most valuable contribution. Follow
  [docs/adding-a-provider.md](docs/adding-a-provider.md); the SQL Server provider is the
  reference implementation. Keep all dialect-specific code inside your provider project —
  `DataverseExporter.Core` must stay database-agnostic.
- **Pull requests** — keep them focused, build with `dotnet build -c Release`, and describe
  what you tested against a real Dataverse environment (or why that wasn't needed).
- **Style** — match the existing code: file-scoped namespaces, records for data shapes,
  comments only where they explain a constraint the code can't express.

## Development setup

```bash
git clone https://github.com/<you>/dataverse-exporter
cd dataverse-exporter
dotnet build
```

To run end-to-end you need a Dataverse environment (a free
[developer plan](https://powerapps.microsoft.com/developerplan/) works) and a target
database. Start small: `"Export": { "SchemaOnly": true, "IncludeEntities": ["account"] }`.
