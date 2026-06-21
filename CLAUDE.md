# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

NSchema.SqlServer is the SQL Server provider for [NSchema](https://github.com/nschema-org/NSchema), a schema-migration framework. It plugs SQL Server-specific implementations of NSchema's `ISchemaProvider` (introspection) and `ISqlGenerator` (DDL generation) into the host application via `NSchemaApplicationBuilder.UseSqlServerSchema(...)`.

Target framework: `net10.0`. C# `LangVersion=latest` with nullable reference types and `TreatWarningsAsErrors=true`. Requires **SQL Server 2016 SP1+** (views/routines use `CREATE OR ALTER`).

## Commands

- Build: `dotnet build NSchema.SqlServer.slnx`
- Test (all): `dotnet test NSchema.SqlServer.slnx`
- Test (single): `dotnet test --filter "FullyQualifiedName~SqlServerSqlGeneratorSnapshotTests.MethodName"`
- Pack (matches CI output): `dotnet pack src/NSchema.SqlServer/NSchema.SqlServer.csproj -c Release`

Integration tests use **Testcontainers** to spin up `mcr.microsoft.com/mssql/server:2022-latest` — Docker must be running locally. The snapshot tests (`SqlServerSqlGeneratorSnapshotTests`) assert on emitted SQL text and need no Docker.

CI/CD runs through an external orchestrator at `nschema-org/NSchema` (`build/build/NSchema.Build`) rather than raw `dotnet` commands — see `.github/workflows/cicd.yml`. The build pipeline expects `Build__ProjectFile=src/NSchema.SqlServer/NSchema.SqlServer.csproj`.

## Local NSchema.Core dependency

This provider pins **NSchema.Core 3.2.0**, which carries two SQL-Server-enabling additions: the column's final type/nullability on `AlterColumnType` / `AlterColumnNullability` (SQL Server's `ALTER COLUMN` must restate both at once), and an inline `Body` on `Trigger` (SQL Server triggers run a body, not a function). When iterating on Core alongside this repo, rebuild and re-push the Core package to the local cache after any change:

```bash
cd ../NSchema.Core
dotnet build src/NSchema.Core/NSchema.Core.csproj --no-incremental
rm -rf ~/.nuget/packages/nschema.core/<version>
dotnet nuget push src/NSchema.Core/bin/Debug/NSchema.Core.<version>.nupkg -s ~/.nuget/packages
cd ../NSchema.SqlServer
dotnet restore --force
```

## Architecture

Two service registrations make up the entire public surface; everything else is `internal`:

- **`SqlServerSchemaProvider`** (`Sql/SqlServerSchemaProvider.cs`) — reads the live database via `sys.*` catalog queries (parameterised by an optional schema-name filter; `null`/empty means all user schemas) and assembles a `DatabaseSchema`. System and fixed-role schemas are filtered out. View bodies and routine modules come from `sys.sql_modules` (the DB's stored form, with the `CREATE … AS` header stripped) so `apply` → `plan` round-trips; stored defaults/checks/index filters are unwrapped of SQL Server's parenthesis padding; sequence options are normalised so engine defaults fold to `null`; comments are read from `sys.extended_properties` (`MS_Description`).
- **`SqlServerSqlGenerator`** (`Sql/SqlServerSqlGenerator.cs`) — translates a `MigrationPlan` into T-SQL. Identifiers are bracket-quoted. The notable SQL Server adaptations: a column's type and nullability are restated together in one `ALTER COLUMN` (the paired action is folded — relies on NSchema.Core 3.1.0 carrying the final type/nullability); defaults are named constraints, added inline/via `ADD DEFAULT` and dropped via a small dynamic-SQL block that looks up the auto-generated name; renames go through `sp_rename`; views and routines use `CREATE OR ALTER`; comments are `sp_add/update/dropextendedproperty`; triggers carry an inline body (`CREATE OR ALTER TRIGGER … AS <body>`, only the SQL-Server-expressible `AFTER`/`INSTEAD OF` statement-level form). Features SQL Server can't model (enums, domains, composite types, extensions, exclusion constraints, schema renames, materialized views, in-place identity/computed changes, and `BEFORE`/row-level/`WHEN`/function-style triggers) throw `NotSupportedException`.

`NSchemaApplicationBuilderExtensions` and `SqlTypeSqlServerExtensions` use C# 14 **extension blocks** (`extension(...) { ... }`) — not classic `this`-parameter extension methods. Editing them requires `LangVersion=latest` / the .NET 10 SDK.

Central package management is on (`Directory.Packages.props`) — add versions there, not in csproj files.
