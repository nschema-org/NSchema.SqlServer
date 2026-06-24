# Changelog

All notable changes to NSchema.SqlServer will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project (mostly) adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Versioning policy

This package uses **lockstep major versioning** with the `NSchema.Core` package: `NSchema.SqlServer X.*.*` requires `NSchema.Core X.*.*`, so version compatibility is always clear.

As a consequence, breaking changes that are specific to this provider (rather than the core API) are signalled by a **minor version bump** rather than a major one, and called out explicitly in this changelog.

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
