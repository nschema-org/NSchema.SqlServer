# Changelog

All notable changes to NSchema.SqlServer will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project (mostly) adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Versioning policy

This package uses **lockstep major versioning** with the `NSchema.Core` package: `NSchema.SqlServer X.*.*` requires `NSchema.Core X.*.*`, so version compatibility is always clear.

As a consequence, breaking changes that are specific to this provider (rather than the core API) are signalled by a **minor version bump** rather than a major one, and called out explicitly in this changelog.

## [5.2.0] - 2026-08-03

### Added

- **The engine's type vocabulary is captured.** Introspection now records the types SQL Server provides (`sys.types`, table types excluded) as `NativeType`s in the snapshot, spelled in the model's canonical names, alongside user-defined alias and CLR types. With a captured vocabulary, a plan can verify every type the project references and report an unresolvable reference at plan time.
- **SQL Server equivalence rules.** `UseSqlServer` now registers `SqlServerSqlEquivalence`: a type qualifier naming `dbo` or `sys` folds away when comparing, so `sys.money` and a declared `money` read as one type.
- **`sys` is reported as a schema SQL Server provides**, alongside `dbo`.

### Fixed

- **User-defined types no longer lose their schema.** A column typed by a UDT outside `dbo` previously introspected as a bare name; it now captures fully qualified, so type references resolve to the right object.

## [5.1.0] - 2026-08-02

### Fixed

- **`dbo` is reported as a schema SQL Server provides.** It is a container rather than something a migration creates, and declaring it is a warning.

## [5.0.0] - 2026-08-01

Tracks the NSchema.Core 5.0 rearchitecture (requires `NSchema.Core 5.0.0-alpha.1`).

### Changed

- **`SqlServerSqlDialect` replaces the SQL generator.** The provider now plugs into Core's `SqlDialect` seam, rendering one migration action at a time. Features SQL Server cannot express (schema renames, materialized views, exclusion constraints, in-place identity/computed-column changes, `BEFORE`/row-level/`WHEN`/function-style triggers, enums, domains, composite types, extensions) now surface as error diagnostics on the plan instead of throwing `NotSupportedException`.
- **`SqlServerDatabaseIntrospector` replaces the schema provider.** It implements Core's `IDatabaseIntrospector`, reading the live database into the new `NSchema.Model` schema model scoped by a `PlanningScope`.
- **`UseSqlServer(...)` replaces `UseSqlServerSchema(...)`, and `UseSqlServerDialect()` replaces `UseSqlServerGenerator()`.** Same overloads and registrations under the new Core seams.
- **The plugin is configured by a `DATABASE` statement.** `SqlServerPlugin` implements `INSchemaDatabasePlugin`: `Configure` takes the core's typed `PluginSettings` and returns a `Result` whose diagnostics carry any configuration errors. Environment overrides are the engine's `NSCHEMA_DATABASE_<SETTING>` now; the provider reads no environment variables of its own.

### Added

- **`new` asks for the connection details.** The plugin declares server, database, authentication and username as scaffolding questions and composes the answers into the `connection_string` it writes. The password is deliberately not asked for — it belongs in `NSCHEMA_DATABASE_PASSWORD`.
- Supports change-event scripts (`SCRIPT '<name>' RUN ON <event> <path>`): the script's SQL is executed verbatim at its planned position in the migration.

## [4.0.0] - 2026-07-01

### Added

- Added plugin manifest to allow for automatic registration of the provider coming in `NSchema 4.0.0.

## [3.0.1] - 2026-06-24

## Fixed

- Fixes the ability to drop schemas by updating to `NSchema.Core 3.3.0` that properly emits `DROP` statements for schema children.

## [3.0.0] - 2026-06-21

First release of the SQL Server provider for NSchema, tracking NSchema 3.2.0 (and requiring `NSchema.Core` 3.2.0 for in-place column alteration and inline-body trigger support).

### Added

- `NSchemaApplicationBuilder.UseSqlServerSchema(...)` extensions for registering the provider. Overloads for a connection string and a `SqlConnectionStringBuilder` configuration delegate, plus a no-arg form for a connection registered elsewhere, and `UseSqlServerGenerator()` for registering only the SQL generator.
- `SqlServerSchemaProvider` implements `ISchemaProvider` to reads the live database from the `sys.*` catalog views (tables, columns with identity/computed/default, primary keys, foreign keys, unique and check constraints, indexes with `INCLUDE`/filters, views, sequences, functions/procedures, table grants, triggers, and `MS_Description` extended-property comments).
- `SqlServerSqlGenerator` implements `ISqlGenerator` to translates an NSchema `MigrationPlan` into T-SQL: bracket-quoted identifiers, `IDENTITY(seed, increment)`, persisted computed columns, `CREATE OR ALTER` views and routines, extended-property comments, and a folded `ALTER COLUMN` for paired type/nullability changes. Features SQL Server has no equivalent for raise a clear `NotSupportedException`.
- `SqlType.Money`, `SqlType.Xml`, and `SqlType.RowVersion` extension helpers for SQL Server-specific column types.

[3.0.0]: https://github.com/nschema-org/NSchema.SqlServer/releases/tag/v3.0.0
