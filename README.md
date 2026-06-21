# ![NSchema](https://raw.githubusercontent.com/nschema-org/NSchema.Docs/main/assets/nschema-logo-horizontal.png)

[![NSchema.SqlServer](https://github.com/nschema-org/NSchema.SqlServer/actions/workflows/cicd.yml/badge.svg)](https://github.com/nschema-org/NSchema.SqlServer/actions/workflows/cicd.yml)

# NSchema.SqlServer

SQL Server provider for [NSchema](https://github.com/nschema-org/NSchema), the declarative database schema migration tool for .NET. It plugs SQL Server introspection and DDL generation into NSchema via [Microsoft.Data.SqlClient](https://learn.microsoft.com/sql/connect/ado-net/microsoft-ado-net-sql-server).

Most users should use the [NSchema CLI](https://github.com/nschema-org/NSchema), which already includes this provider. Add this package directly only when [embedding the engine](https://nschema.dev/library/embedding/) in your own application.

## Installation

```sh
dotnet add package NSchema.Core
dotnet add package NSchema.SqlServer
```

## Requirements

- **SQL Server 2016 SP1 or newer**

## Scope

This provider implements all the parts of NSchema's model that are compatible with SQL Server:

- **Supported:** schemas, tables, columns (with `DEFAULT`, `IDENTITY`, and persisted computed columns), primary keys, foreign keys, unique constraints, check constraints, indexes (including `INCLUDE` columns and filtered indexes), views, sequences, scalar/table functions and stored procedures, table-level `GRANT`s, and documentation comments (stored as `MS_Description` extended properties).
- **Identifiers** are emitted with brackets (`[schema].[name]`).
- **Column changes.** SQL Server's `ALTER COLUMN` restates the column's whole type and nullability at once, so NSchema.Core (3.1.0+) supplies both on the migration actions and the generator folds a paired type/nullability change into one statement.
- **Not supported (no SQL Server equivalent):** triggers (NSchema models a trigger as calling a function, which SQL Server's trigger bodies are not), enums, domains, composite types, extensions, exclusion constraints, schema renames, and materialized (indexed) views. In-place changes to an identity's seed/increment or a computed column's expression require a table rebuild and raise a clear `NotSupportedException`. These all raise `NotSupportedException` rather than emitting partial SQL.

NSchema's canonical types map to SQL Server's: `boolean` → `bit`, `double` → `float`, `datetime` → `datetime2`, `guid` → `uniqueidentifier`, unbounded `varchar`/`nvarchar`/`varbinary` → `(max)`, and so on.

## Documentation

Full documentation lives at **[nschema.dev](https://nschema.dev)**:

- [Embedding the engine](https://nschema.dev/library/embedding/)

## License

See [LICENSE](LICENSE).
