using System.Data.Common;
using System.Text.RegularExpressions;
using NSchema.Schema;
using NSchema.Schema.Model;
using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Constraints;
using NSchema.Schema.Model.Indexes;
using NSchema.Schema.Model.Routines;
using NSchema.Schema.Model.Schemas;
using NSchema.Schema.Model.Sequences;
using NSchema.Schema.Model.Tables;
using NSchema.Schema.Model.Views;

namespace NSchema.SqlServer.Sql;

/// <summary>
/// Reads a live SQL Server database into an NSchema <see cref="DatabaseSchema"/> via the <c>sys.*</c> catalog views.
/// </summary>
/// <remarks>
/// A fixed sequence of independent queries runs against one connection opened from the injected
/// <see cref="SqlServerConnectionSource"/>; each is scoped by an optional schema-name filter (<c>null</c>/empty means
/// "all user schemas"). Only the surfaces the generator can produce are introspected — triggers, enums, domains,
/// composite types and extensions are out of scope. View bodies and routine modules come from
/// <c>sys.sql_modules</c> (the database's stored form) so an <c>apply</c> round-trips; identity, computed columns and
/// extended-property comments are read from their dedicated catalog views.
/// </remarks>
internal sealed partial class SqlServerSchemaProvider(SqlServerConnectionSource source) : ISchemaProvider
{
    private const string DescriptionProperty = "MS_Description";

    // Built-in schemas (sys/INFORMATION_SCHEMA/guest) and the fixed database-role schemas are never part of a declared
    // user schema, so they are filtered out everywhere.
    private const string SystemSchemaFilter = """
        s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest', 'db_owner', 'db_accessadmin', 'db_securityadmin',
                       'db_ddladmin', 'db_backupoperator', 'db_datareader', 'db_datawriter', 'db_denydatareader',
                       'db_denydatawriter')
        """;

    private const string SchemaScopeFilter = "(@schemas IS NULL OR s.name IN (SELECT value FROM STRING_SPLIT(@schemas, ',')))";

    public async ValueTask<DatabaseSchema> GetSchema(string[]? schemaNames = null, CancellationToken cancellationToken = default)
    {
        var schemas = schemaNames is { Length: > 0 } ? schemaNames : null;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);

        var schemaNamesInDb = await QuerySchemas(connection, schemas, cancellationToken);
        var tables = await QueryTables(connection, schemas, cancellationToken);
        var columns = await QueryColumns(connection, schemas, cancellationToken);
        var primaryKeys = await QueryKeyConstraints(connection, schemas, "PK", cancellationToken);
        var uniqueConstraints = await QueryKeyConstraints(connection, schemas, "UQ", cancellationToken);
        var foreignKeys = await QueryForeignKeys(connection, schemas, cancellationToken);
        var checkConstraints = await QueryCheckConstraints(connection, schemas, cancellationToken);
        var indexes = await QueryIndexes(connection, schemas, cancellationToken);
        var views = await QueryViews(connection, schemas, cancellationToken);
        var sequences = await QuerySequences(connection, schemas, cancellationToken);
        var routines = await QueryRoutines(connection, schemas, cancellationToken);
        var tableGrants = await QueryTableGrants(connection, schemas, cancellationToken);

        var schemaComments = await QueryComments(connection, schemas, SchemaCommentSql, cancellationToken);
        var tableComments = await QueryComments(connection, schemas, ObjectCommentSql("U"), cancellationToken);
        var viewComments = await QueryComments(connection, schemas, ObjectCommentSql("V"), cancellationToken);
        var sequenceComments = await QueryComments(connection, schemas, SequenceCommentSql, cancellationToken);
        var routineComments = await QueryComments(connection, schemas, RoutineCommentSql, cancellationToken);
        var columnComments = await QueryNestedComments(connection, schemas, ColumnCommentSql, cancellationToken);
        var indexComments = await QueryNestedComments(connection, schemas, IndexCommentSql, cancellationToken);
        var constraintComments = await QueryNestedComments(connection, schemas, ConstraintCommentSql, cancellationToken);

        return Build(schemaNamesInDb, tables, columns, primaryKeys, uniqueConstraints, foreignKeys, checkConstraints,
            indexes, views, sequences, routines, tableGrants,
            schemaComments, tableComments, viewComments, sequenceComments, routineComments,
            columnComments, indexComments, constraintComments);
    }

    // ── Row DTOs ────────────────────────────────────────────────────────────────

    private sealed record TableRow(string Schema, string Name);
    private sealed record ColumnRow(string Schema, string Table, string Name, string TypeName, int MaxLength, int Precision, int Scale,
        bool IsNullable, bool IsIdentity, long? Seed, long? Increment, string? Computed, string? Default);
    private sealed record KeyColumnRow(string Schema, string Table, string Constraint, string Column);
    private sealed record ForeignKeyRow(string Schema, string Table, string Constraint, string Column,
        string RefSchema, string RefTable, string RefColumn, int DeleteAction, int UpdateAction);
    private sealed record CheckRow(string Schema, string Table, string Name, string Definition);
    private sealed record IndexColumnRow(string Schema, string Table, string Index, string Column, bool IsIncluded, bool IsDescending, bool IsUnique, string? Filter);
    private sealed record ViewRow(string Schema, string Name, string Definition);
    private sealed record SequenceRow(string Schema, string Name, string TypeName, long Start, long Increment, long Min, long Max, bool Cycle, bool IsCached, int? CacheSize);
    private sealed record RoutineRow(string Schema, string Name, bool IsProcedure, string Definition);
    private sealed record TableGrantRow(string Schema, string Table, string Role, string Privilege);

    // ── Queries ───────────────────────────────────────────────────────────────────

    private static void AddSchemasParameter(DbCommand cmd, string[]? schemas)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@schemas";
        parameter.Value = schemas is null ? DBNull.Value : string.Join(',', schemas);
        cmd.Parameters.Add(parameter);
    }

    private static async Task<List<T>> Query<T>(DbConnection connection, string sql, string[]? schemas, Func<DbDataReader, T> read, CancellationToken ct)
    {
        var rows = new List<T>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddSchemasParameter(command, schemas);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static Task<List<string>> QuerySchemas(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name
        FROM sys.schemas s
        WHERE {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name
        """, schemas, r => r.GetString(0), ct);

    private static Task<List<TableRow>> QueryTables(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name, t.name
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, t.name
        """, schemas, r => new TableRow(r.GetString(0), r.GetString(1)), ct);

    private static Task<List<ColumnRow>> QueryColumns(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name, t.name, col.name, typ.name,
               col.max_length, col.precision, col.scale,
               col.is_nullable, col.is_identity,
               CAST(ic.seed_value AS bigint), CAST(ic.increment_value AS bigint),
               cc.definition, dc.definition
        FROM sys.columns col
        JOIN sys.tables t ON t.object_id = col.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.types typ ON typ.user_type_id = col.user_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = col.object_id AND ic.column_id = col.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = col.object_id AND cc.column_id = col.column_id
        LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = col.object_id AND dc.parent_column_id = col.column_id
        WHERE {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, t.name, col.column_id
        """, schemas, r => new ColumnRow(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetInt16(4), r.GetByte(5), r.GetByte(6),
            r.GetBoolean(7), r.GetBoolean(8),
            r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10),
            r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12)), ct);

    private static Task<List<KeyColumnRow>> QueryKeyConstraints(DbConnection c, string[]? schemas, string type, CancellationToken ct) => Query(c, $"""
        SELECT s.name, t.name, kc.name, col.name
        FROM sys.key_constraints kc
        JOIN sys.tables t ON t.object_id = kc.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
        JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
        WHERE kc.type = '{type}' AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, t.name, kc.name, ic.key_ordinal
        """, schemas, r => new KeyColumnRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);

    private static Task<List<ForeignKeyRow>> QueryForeignKeys(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name, t.name, fk.name, pc.name, rs.name, rt.name, rc.name,
               fk.delete_referential_action, fk.update_referential_action
        FROM sys.foreign_keys fk
        JOIN sys.tables t ON t.object_id = fk.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, t.name, fk.name, fkc.constraint_column_id
        """, schemas, r => new ForeignKeyRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetString(5), r.GetString(6), r.GetByte(7), r.GetByte(8)), ct);

    private static Task<List<CheckRow>> QueryCheckConstraints(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name, t.name, cc.name, cc.definition
        FROM sys.check_constraints cc
        JOIN sys.tables t ON t.object_id = cc.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, t.name, cc.name
        """, schemas, r => new CheckRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);

    private static Task<List<IndexColumnRow>> QueryIndexes(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name, t.name, i.name, col.name, ic.is_included_column, ic.is_descending_key, i.is_unique, i.filter_definition
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
        WHERE i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.type <> 0 AND i.is_hypothetical = 0
          AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, t.name, i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id
        """, schemas, r => new IndexColumnRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7)), ct);

    private static Task<List<ViewRow>> QueryViews(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name, v.name, m.definition
        FROM sys.views v
        JOIN sys.schemas s ON s.schema_id = v.schema_id
        JOIN sys.sql_modules m ON m.object_id = v.object_id
        WHERE {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, v.name
        """, schemas, r => new ViewRow(r.GetString(0), r.GetString(1), r.GetString(2)), ct);

    private static Task<List<SequenceRow>> QuerySequences(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name, seq.name, typ.name,
               CAST(seq.start_value AS bigint), CAST(seq.increment AS bigint),
               CAST(seq.minimum_value AS bigint), CAST(seq.maximum_value AS bigint),
               seq.is_cycling, seq.is_cached, seq.cache_size
        FROM sys.sequences seq
        JOIN sys.schemas s ON s.schema_id = seq.schema_id
        JOIN sys.types typ ON typ.user_type_id = seq.user_type_id
        WHERE {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, seq.name
        """, schemas, r => new SequenceRow(r.GetString(0), r.GetString(1), r.GetString(2),
            r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
            r.GetBoolean(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetInt32(9)), ct);

    private static Task<List<RoutineRow>> QueryRoutines(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name, o.name, CASE WHEN o.type = 'P' THEN 1 ELSE 0 END, m.definition
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.sql_modules m ON m.object_id = o.object_id
        WHERE o.type IN ('P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0
          AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, o.name
        """, schemas, r => new RoutineRow(r.GetString(0), r.GetString(1), r.GetInt32(2) == 1, r.GetString(3)), ct);

    private static Task<List<TableGrantRow>> QueryTableGrants(DbConnection c, string[]? schemas, CancellationToken ct) => Query(c, $"""
        SELECT s.name, o.name, dpr.name, dp.permission_name
        FROM sys.database_permissions dp
        JOIN sys.objects o ON o.object_id = dp.major_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.database_principals dpr ON dpr.principal_id = dp.grantee_principal_id
        WHERE dp.class = 1 AND dp.minor_id = 0 AND dp.state IN ('G', 'W') AND o.type = 'U'
          AND dp.permission_name IN ('SELECT', 'INSERT', 'UPDATE', 'DELETE')
          AND dpr.name NOT IN ('public', 'dbo')
          AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        ORDER BY s.name, o.name, dpr.name, dp.permission_name
        """, schemas, r => new TableGrantRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);

    // ── Comments (extended properties) ──────────────────────────────────────────────

    private static async Task<Dictionary<(string, string), string>> QueryComments(DbConnection c, string[]? schemas, string sql, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), string>();
        foreach (var (schema, name, comment) in await Query(c, sql, schemas, r => (r.GetString(0), r.GetString(1), r.GetString(2)), ct))
        {
            result[(schema, name)] = comment;
        }

        return result;
    }

    private static async Task<Dictionary<(string, string, string), string>> QueryNestedComments(DbConnection c, string[]? schemas, string sql, CancellationToken ct)
    {
        var result = new Dictionary<(string, string, string), string>();
        foreach (var (schema, parent, child, comment) in await Query(c, sql, schemas, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct))
        {
            result[(schema, parent, child)] = comment;
        }

        return result;
    }

    // class 3 = schema-level property; the value is sql_variant, read as nvarchar.
    private static readonly string SchemaCommentSql = $"""
        SELECT s.name, s.name, CAST(ep.value AS nvarchar(max))
        FROM sys.extended_properties ep
        JOIN sys.schemas s ON s.schema_id = ep.major_id
        WHERE ep.class = 3 AND ep.name = '{DescriptionProperty}' AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        """;

    private static readonly string SequenceCommentSql = $"""
        SELECT s.name, o.name, CAST(ep.value AS nvarchar(max))
        FROM sys.extended_properties ep
        JOIN sys.objects o ON o.object_id = ep.major_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = '{DescriptionProperty}' AND o.type = 'SO'
          AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        """;

    private static readonly string RoutineCommentSql = $"""
        SELECT s.name, o.name, CAST(ep.value AS nvarchar(max))
        FROM sys.extended_properties ep
        JOIN sys.objects o ON o.object_id = ep.major_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = '{DescriptionProperty}' AND o.type IN ('P', 'FN', 'IF', 'TF')
          AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        """;

    // class 1, minor_id 0 = the object itself; filtered to the requested object type (U = table, V = view).
    private static string ObjectCommentSql(string objectType) => $"""
        SELECT s.name, o.name, CAST(ep.value AS nvarchar(max))
        FROM sys.extended_properties ep
        JOIN sys.objects o ON o.object_id = ep.major_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = '{DescriptionProperty}' AND o.type = '{objectType}'
          AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        """;

    // class 1, minor_id = column_id > 0 = a column property.
    private static readonly string ColumnCommentSql = $"""
        SELECT s.name, t.name, col.name, CAST(ep.value AS nvarchar(max))
        FROM sys.extended_properties ep
        JOIN sys.tables t ON t.object_id = ep.major_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.columns col ON col.object_id = ep.major_id AND col.column_id = ep.minor_id
        WHERE ep.class = 1 AND ep.minor_id > 0 AND ep.name = '{DescriptionProperty}'
          AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        """;

    // class 7 = index property (major_id = table object, minor_id = index_id).
    private static readonly string IndexCommentSql = $"""
        SELECT s.name, t.name, i.name, CAST(ep.value AS nvarchar(max))
        FROM sys.extended_properties ep
        JOIN sys.tables t ON t.object_id = ep.major_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.indexes i ON i.object_id = ep.major_id AND i.index_id = ep.minor_id
        WHERE ep.class = 7 AND ep.name = '{DescriptionProperty}'
          AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        """;

    // A constraint is an object in its own right; its parent table is found via the constraint's parent_object_id.
    private static readonly string ConstraintCommentSql = $"""
        SELECT s.name, t.name, con.name, CAST(ep.value AS nvarchar(max))
        FROM sys.extended_properties ep
        JOIN sys.objects con ON con.object_id = ep.major_id
        JOIN sys.tables t ON t.object_id = con.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = '{DescriptionProperty}'
          AND con.type IN ('PK', 'UQ', 'F', 'C', 'D')
          AND {SystemSchemaFilter} AND {SchemaScopeFilter}
        """;

    // ── Model assembly ──────────────────────────────────────────────────────────

    private static DatabaseSchema Build(
        List<string> schemaNames,
        List<TableRow> tables,
        List<ColumnRow> columns,
        List<KeyColumnRow> primaryKeys,
        List<KeyColumnRow> uniqueConstraints,
        List<ForeignKeyRow> foreignKeys,
        List<CheckRow> checkConstraints,
        List<IndexColumnRow> indexes,
        List<ViewRow> views,
        List<SequenceRow> sequences,
        List<RoutineRow> routines,
        List<TableGrantRow> tableGrants,
        Dictionary<(string, string), string> schemaComments,
        Dictionary<(string, string), string> tableComments,
        Dictionary<(string, string), string> viewComments,
        Dictionary<(string, string), string> sequenceComments,
        Dictionary<(string, string), string> routineComments,
        Dictionary<(string, string, string), string> columnComments,
        Dictionary<(string, string, string), string> indexComments,
        Dictionary<(string, string, string), string> constraintComments)
    {
        var tablesBySchema = tables
            .GroupBy(t => t.Schema)
            .ToDictionary(g => g.Key, g => g.Select(t => BuildTable(t, columns, primaryKeys, uniqueConstraints, foreignKeys,
                checkConstraints, indexes, tableGrants, tableComments, columnComments, indexComments, constraintComments)).ToList());

        var viewsBySchema = views
            .GroupBy(v => v.Schema)
            .ToDictionary(g => g.Key, g => g.Select(v => new View(v.Name, ExtractViewBody(v.Definition),
                Comment: viewComments.GetValueOrDefault((v.Schema, v.Name)))).ToList());

        var sequencesBySchema = sequences
            .GroupBy(s => s.Schema)
            .ToDictionary(g => g.Key, g => g.Select(s => new Sequence(s.Name, NormalizeSequenceOptions(s),
                Comment: sequenceComments.GetValueOrDefault((s.Schema, s.Name)))).ToList());

        var routinesBySchema = routines
            .GroupBy(r => r.Schema)
            .ToDictionary(g => g.Key, g => g.Select(r => BuildRoutine(r, routineComments.GetValueOrDefault((r.Schema, r.Name)))).ToList());

        var allSchemas = schemaNames
            .Union(tablesBySchema.Keys)
            .Union(viewsBySchema.Keys)
            .Union(sequencesBySchema.Keys)
            .Union(routinesBySchema.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);

        var definitions = allSchemas
            .Select(name => new SchemaDefinition(name,
                Comment: schemaComments.GetValueOrDefault((name, name)),
                Tables: tablesBySchema.GetValueOrDefault(name, []),
                Views: viewsBySchema.GetValueOrDefault(name, []),
                Sequences: sequencesBySchema.GetValueOrDefault(name, []),
                Routines: routinesBySchema.GetValueOrDefault(name, [])))
            .ToList();

        return new DatabaseSchema(definitions);
    }

    private static Table BuildTable(
        TableRow table,
        List<ColumnRow> allColumns,
        List<KeyColumnRow> allPrimaryKeys,
        List<KeyColumnRow> allUniqueConstraints,
        List<ForeignKeyRow> allForeignKeys,
        List<CheckRow> allChecks,
        List<IndexColumnRow> allIndexes,
        List<TableGrantRow> allGrants,
        Dictionary<(string, string), string> tableComments,
        Dictionary<(string, string, string), string> columnComments,
        Dictionary<(string, string, string), string> indexComments,
        Dictionary<(string, string, string), string> constraintComments)
    {
        bool Owns(string schema, string tableName) => schema == table.Schema && tableName == table.Name;

        var cols = allColumns
            .Where(c => Owns(c.Schema, c.Table))
            .Select(c => new Column(c.Name, MapSqlType(c.TypeName, c.MaxLength, c.Precision, c.Scale),
                IsNullable: c.IsNullable,
                IsIdentity: c.IsIdentity,
                DefaultExpression: c.Computed is null ? StripParens(c.Default) : null,
                Comment: columnComments.GetValueOrDefault((c.Schema, c.Table, c.Name)),
                IdentityOptions: c.IsIdentity ? new IdentityOptions(c.Seed, null, c.Increment) : null,
                GeneratedExpression: c.Computed is null ? null : StripParens(c.Computed)))
            .ToList();

        var primaryKey = allPrimaryKeys
            .Where(k => Owns(k.Schema, k.Table))
            .GroupBy(k => k.Constraint)
            .Select(g => new PrimaryKey(g.Key, g.Select(k => k.Column).ToList(),
                constraintComments.GetValueOrDefault((table.Schema, table.Name, g.Key))))
            .FirstOrDefault();

        var uniques = allUniqueConstraints
            .Where(k => Owns(k.Schema, k.Table))
            .GroupBy(k => k.Constraint)
            .Select(g => new UniqueConstraint(g.Key, g.Select(k => k.Column).ToList(),
                constraintComments.GetValueOrDefault((table.Schema, table.Name, g.Key))))
            .ToList();

        var foreignKeys = allForeignKeys
            .Where(f => Owns(f.Schema, f.Table))
            .GroupBy(f => f.Constraint)
            .Select(g =>
            {
                var first = g.First();
                return new ForeignKey(g.Key,
                    g.Select(f => f.Column).ToList(),
                    first.RefSchema, first.RefTable, g.Select(f => f.RefColumn).ToList(),
                    MapReferentialAction(first.DeleteAction), MapReferentialAction(first.UpdateAction),
                    constraintComments.GetValueOrDefault((table.Schema, table.Name, g.Key)));
            })
            .ToList();

        var checks = allChecks
            .Where(c => Owns(c.Schema, c.Table))
            .Select(c => new CheckConstraint(c.Name, StripParens(c.Definition) ?? c.Definition,
                constraintComments.GetValueOrDefault((table.Schema, table.Name, c.Name))))
            .ToList();

        var indexes = allIndexes
            .Where(i => Owns(i.Schema, i.Table))
            .GroupBy(i => i.Index)
            .Select(g => BuildIndex(g.Key, g.ToList(), indexComments.GetValueOrDefault((table.Schema, table.Name, g.Key))))
            .ToList();

        var grants = allGrants
            .Where(g => Owns(g.Schema, g.Table))
            .GroupBy(g => g.Role)
            .Select(g => new TableGrant(g.Key, ToTablePrivilege(g.Select(r => r.Privilege))))
            .ToList();

        return new Table(table.Name,
            PrimaryKey: primaryKey,
            Comment: tableComments.GetValueOrDefault((table.Schema, table.Name)),
            Columns: cols,
            ForeignKeys: foreignKeys,
            UniqueConstraints: uniques,
            CheckConstraints: checks,
            Indexes: indexes,
            Grants: grants);
    }

    private static TablePrivilege ToTablePrivilege(IEnumerable<string> privileges) =>
        privileges.Aggregate(TablePrivilege.None, (current, privilege) => current | privilege switch
        {
            "SELECT" => TablePrivilege.Select,
            "INSERT" => TablePrivilege.Insert,
            "UPDATE" => TablePrivilege.Update,
            "DELETE" => TablePrivilege.Delete,
            _ => TablePrivilege.None,
        });

    private static TableIndex BuildIndex(string name, List<IndexColumnRow> columns, string? comment)
    {
        var keys = columns.Where(c => !c.IsIncluded)
            .Select(c => new IndexColumn(c.Column, Sort: c.IsDescending ? IndexSort.Descending : IndexSort.Default))
            .ToList();
        var include = columns.Where(c => c.IsIncluded).Select(c => c.Column).ToList();
        var first = columns[0];
        return new TableIndex(name, keys, first.IsUnique, comment, StripParens(first.Filter), Method: null, include);
    }

    private static Routine BuildRoutine(RoutineRow row, string? comment)
    {
        var (arguments, definition) = SplitRoutineModule(row.Definition, row.Name);
        return new Routine(row.Name, row.IsProcedure ? RoutineKind.Procedure : RoutineKind.Function, arguments, definition,
            Comment: comment);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────────

    private static SqlType MapSqlType(string typeName, int maxLength, int precision, int scale) => typeName switch
    {
        "bit" => SqlType.Boolean,
        "tinyint" => SqlType.TinyInt,
        "smallint" => SqlType.SmallInt,
        "int" => SqlType.Int,
        "bigint" => SqlType.BigInt,
        "real" => SqlType.Float,
        "float" => SqlType.Double,
        "decimal" or "numeric" => SqlType.Decimal(precision, scale),
        "char" => SqlType.Char(maxLength),
        "nchar" => SqlType.NChar(maxLength / 2),
        "varchar" => maxLength == -1 ? SqlType.VarChar() : SqlType.VarChar(maxLength),
        "nvarchar" => maxLength == -1 ? SqlType.NVarChar() : SqlType.NVarChar(maxLength / 2),
        "date" => SqlType.Date,
        "time" => SqlType.Time,
        "datetime2" => SqlType.DateTime,
        "datetimeoffset" => SqlType.DateTimeOffset,
        "uniqueidentifier" => SqlType.Guid,
        "binary" => SqlType.Binary(maxLength),
        "varbinary" => maxLength == -1 ? SqlType.VarBinary() : SqlType.VarBinary(maxLength),
        _ => SqlType.Custom(typeName),
    };

    private static ReferentialAction MapReferentialAction(int action) => action switch
    {
        1 => ReferentialAction.Cascade,
        2 => ReferentialAction.SetNull,
        3 => ReferentialAction.SetDefault,
        _ => ReferentialAction.NoAction,
    };

    // SQL Server engine defaults are folded to null so a bare CREATE SEQUENCE round-trips to an all-null
    // SequenceOptions and the core's record equality sees no drift.
    private static SequenceOptions NormalizeSequenceOptions(SequenceRow row)
    {
        var ascending = row.Increment > 0;
        var (typeMin, typeMax) = row.TypeName switch
        {
            "tinyint" => ((long)byte.MinValue, (long)byte.MaxValue),
            "smallint" => (short.MinValue, short.MaxValue),
            "int" => (int.MinValue, int.MaxValue),
            _ => (long.MinValue, long.MaxValue),
        };

        var defaultMin = ascending ? typeMin : typeMin;
        var defaultMax = typeMax;
        var defaultStart = ascending ? row.Min : row.Max;

        return new SequenceOptions(
            DataType: row.TypeName == "bigint" ? null : SqlType.Parse(row.TypeName),
            StartWith: row.Start == defaultStart ? null : row.Start,
            IncrementBy: row.Increment == 1 ? null : row.Increment,
            MinValue: row.Min == defaultMin ? null : row.Min,
            MaxValue: row.Max == defaultMax ? null : row.Max,
            Cache: row.IsCached ? row.CacheSize : null,
            Cycle: row.Cycle);
    }

    // SQL Server wraps a stored default/check/filter in one or more layers of parentheses (e.g. ((0)), (N'x')).
    // Strip every layer that fully encloses the expression so the stored form matches what an author wrote.
    private static string? StripParens(string? expression)
    {
        if (expression is null)
        {
            return null;
        }

        var value = expression.Trim();
        while (value.Length >= 2 && value[0] == '(' && Encloses(value))
        {
            value = value[1..^1].Trim();
        }

        return value;
    }

    // True when the leading '(' matches the trailing ')', i.e. one pair wraps the whole expression.
    private static bool Encloses(string value)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            depth += value[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return i == value.Length - 1;
            }
        }

        return false;
    }

    // sys.sql_modules stores the whole CREATE statement. The body is everything after the first top-level AS that
    // follows the view name; the leading CREATE [OR ALTER] VIEW <name> header (and any WITH clause) is dropped.
    private static string ExtractViewBody(string moduleDefinition)
    {
        var match = ViewHeader().Match(moduleDefinition);
        return (match.Success ? moduleDefinition[match.Length..] : moduleDefinition).Trim();
    }

    // Splits a routine module into the parenthesised argument list and the remaining definition. A procedure declared
    // without parentheses has its arguments taken as the text up to the first AS, which the generator re-wraps in
    // parentheses on the next apply.
    private static (string Arguments, string Definition) SplitRoutineModule(string moduleDefinition, string name)
    {
        var header = RoutineHeader().Match(moduleDefinition);
        var rest = (header.Success ? moduleDefinition[header.Length..] : moduleDefinition).TrimStart();

        if (rest.StartsWith('('))
        {
            var depth = 0;
            for (var i = 0; i < rest.Length; i++)
            {
                depth += rest[i] switch { '(' => 1, ')' => -1, _ => 0 };
                if (depth == 0)
                {
                    return (rest[1..i].Trim(), rest[(i + 1)..].Trim());
                }
            }
        }

        var asIndex = rest.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        return asIndex < 0 ? ("", rest.Trim()) : (rest[..asIndex].Trim(), rest[(asIndex + 1)..].Trim());
    }

    [GeneratedRegex(@"^\s*CREATE\s+(OR\s+ALTER\s+)?VIEW\s+.+?\s+AS\s+", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ViewHeader();

    [GeneratedRegex(@"^\s*CREATE\s+(OR\s+ALTER\s+)?(FUNCTION|PROCEDURE|PROC)\s+(\[[^\]]*\]|[^\s(]+)(\.(\[[^\]]*\]|[^\s(]+))?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RoutineHeader();
}
