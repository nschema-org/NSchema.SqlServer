# Changelog

All notable changes to NSchema.SqlServer will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project (mostly) adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Versioning policy

This package uses **lockstep major versioning** with the `NSchema.Core` package: `NSchema.SqlServer X.*.*` requires `NSchema.Core X.*.*`, so version compatibility is always clear.

As a consequence, breaking changes that are specific to this provider (rather than the core API) are signalled by a **minor version bump** rather than a major one, and called out explicitly in this changelog.

## [Unreleased]

### Added

- **`is_persisted` is introspected**, and `SupportsVirtualGeneratedColumns` is declared.

### Changed

- **The build now writes the full dependency closure beside the assembly** (`CopyLocalLockFileAssemblies`), so a `dotnet build` of this project can be loaded directly by `PLUGIN ( path = '...' )` without being packed first. Package contents are unchanged.

### Fixed

- **Computed columns are no longer all `PERSISTED`.** The keyword was emitted unconditionally, so a column computed on read came back written to storage.
- **`ROWGUIDCOL` is no longer dropped.** It was neither introspected nor rendered, so a column marked as the table's row identifier came back as an ordinary one.
- **Default constraint names are preserved.** A default was emitted unnamed, so SQL Server generated one.
- **`NOT FOR REPLICATION` is no longer dropped** from identity columns or triggers.

## [5.7.0] - 2026-08-09

### Added

- **Clustered indexes round-trip.** `SupportsClustering` is on, so `CLUSTERED` and `NONCLUSTERED` are rendered on indexes, primary keys and unique constraints.
- **XML schema collections round-trip.** `XML_SCHEMA_NAMESPACE` reassembles a collection from `sys.xml_schema_*` into the single document it was declared as, and a typed `xml` column carries the collection it is bound to along with whether it is `DOCUMENT` or `CONTENT`.
- **Constraints SQL Server named for itself are reported.** `is_system_named` is read for key, foreign-key and check constraints, and a `system-named-constraint` warning names them as one grouped finding.
- **XML indexes round-trip.** `sys.xml_indexes` supplies the kind and, for a secondary, the primary whose node table it reads (`i.type` identifies an XML index, since `secondary_type_desc` is null for a primary as well as for a non-XML index).
- **Indexed views round-trip.** Indexes are read from `sys.objects` rather than `sys.tables`, so a view's indexes are introspected onto `View.Indexes` alongside a table's.
- **Schema binding round-trips.** `WITH SCHEMABINDING` is read from `sys.sql_modules.is_schema_bound` onto `View.IsSchemaBound` and written back from it.

## [5.6.0] - 2026-08-06

### Fixed

- **Options clauses on procedures split correctly.** An options clause (`WITH EXECUTE AS …`, `WITH RECOMPILE`) ends the header too and stays with the definition.
- **Comments before a module's `CREATE` no longer derail the split.** `sys.sql_modules` stores the batch as written, comments included; the leading trivia is now stepped over before the header is matched, for routines, views, and triggers.
- **A parameter list ending in a line comment renders intact.** The closing parenthesis is printed on a new line rather than being swallowed by the comment.
- **A parameter-less procedure renders without parentheses.** T-SQL rejects `CREATE PROCEDURE name()`; the empty list is omitted (functions keep theirs).

## [5.5.0] - 2026-08-06

### Added

- **Alias types are domains.** A user-defined alias type (`CREATE TYPE … FROM base`) now introspects as a domain — base type, nullability, and `MS_Description` comment — and the dialect renders domain actions: create, drop, rename (`sp_rename … 'USERDATATYPE'`), recreate, and comment. A domain declaring a default or check constraints remains a clear diagnostic — alias types cannot carry them.

### Changed

- **Alias types left the vocabulary.** They were previously reported as `NativeType`s; a declaration the plan can create and drop is not vocabulary. CLR types remain vocabulary, and table types remain excluded.

### Fixed

- **A view's introspected body is the bare query.** The trailing statement terminator `sys.sql_modules` preserves from the original `CREATE VIEW` is now stripped, so the introspected body matches what an author writes.
- **Introspected comments are trimmed.** An `MS_Description` value with surrounding whitespace cannot be expressed by an NSQL doc comment, so it could never round-trip; values are trimmed on the way in and whitespace-only values are treated as no comment.
- **Unparenthesized procedure headers split correctly.** A procedure declared without parentheses had its parameter list found by searching for a space-padded `AS`, which missed a newline-delimited header `AS` and could match one inside the body (a CTE's, an alias's). The header `AS` is now located by a scan that honors strings, bracketed identifiers, comments, parenthesis depth, and a parameter's own `@name AS type`.

## [5.4.0] - 2026-08-03

### Added

- **Aggregates are a declared limitation.** A project declaring `CREATE AGGREGATE` gets a clear diagnostic — SQL Server aggregates are CLR assemblies the model cannot express — instead of a wrong `CREATE FUNCTION`.

### Changed

- **`datetime` and `datetime2` are two types, and both are preserved.** A user who writes `datetime` gets `datetime`; `datetime2` round-trips verbatim. The model's canonical `datetime` no longer silently upgrades to `datetime2`.
- **System types are captured bare.** A built-in outside the model's vocabulary (`money`, `xml`, both `datetime`s) introspects under its own bare name — the engine's vocabulary is addressed bare — while user-defined types keep their owning schema.

## [5.3.0] - 2026-08-03

### Changed

- **A create is a create.** `CREATE FUNCTION`, `CREATE PROCEDURE`, `CREATE VIEW`, and `CREATE TRIGGER` render without `OR ALTER` when the plan is creating; `CREATE OR ALTER` renders only for the replace and recreate actions, where the plan knows the object exists. (A changed trigger plans as a replacement, rendered `CREATE OR ALTER`.) A create colliding with an object the plan didn't know about now fails loudly instead of silently overwriting it.

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
