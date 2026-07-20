using System.Text;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Indexes;
using NSchema.Model.Routines;
using NSchema.Model.Sequences;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Plan.Backends;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Sequences;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;

namespace NSchema.SqlServer.Sql;

/// <summary>
/// The SQL Server (T-SQL) <see cref="SqlDialect"/>.
/// </summary>
/// <remarks>
/// SQL Server's surface differs from the ANSI base in a few places that shape this dialect:
/// <list type="bullet">
/// <item><b>Identifiers are bracket-quoted.</b></item>
/// <item><b>ALTER COLUMN is monolithic.</b> T-SQL restates a column's full type and nullability in one statement,
/// so both the type and the nullability actions render the column's complete final state (the core supplies the
/// unchanged half on each action).</item>
/// <item><b>Defaults are named constraints.</b> A default is added inline or via <c>ADD DEFAULT … FOR</c>; dropping
/// one requires finding its auto-generated constraint name, done with a small dynamic-SQL block.</item>
/// <item><b>Renames go through <c>sp_rename</c></b> for tables, columns, views, routines and sequences.</item>
/// <item><b>Triggers carry an inline body</b> (<c>… AS &lt;body&gt;</c>); only the SQL Server-expressible facets are
/// accepted — <c>AFTER</c>/<c>INSTEAD OF</c>, statement-level, no <c>WHEN</c>.</item>
/// <item><b>No equivalent (error diagnostics):</b> enums, domains, composite types, extensions, exclusion
/// constraints, schema renames, materialized views, and in-place changes to a computed-column expression or an
/// identity's seed/increment (SQL Server requires a table rebuild).</item>
/// </list>
/// </remarks>
internal sealed class SqlServerSqlDialect : SqlDialect
{
    private const string DescriptionProperty = "MS_Description";
    private const string Source = "sqlserver-dialect";

    /// <inheritdoc />
    protected override string Name => "SQL Server (NSchema.SqlServer)";

    /// <summary>A bracket-quoted identifier; a literal ']' inside a name is doubled.</summary>
    protected override string Quote(SqlIdentifier identifier) => $"[{identifier.Value.Replace("]", "]]")}]";

    /// <summary>A failed rendering with a SQL Server-specific explanation.</summary>
    private static Result<IReadOnlyList<SqlStatement>> Error(FormattedText message) =>
        Result.Failure<IReadOnlyList<SqlStatement>>(Diagnostic.Error(Source, message));

    // ── Schemas ───────────────────────────────────────────────────────────────
    // CREATE SCHEMA / DROP SCHEMA use the base forms. SQL Server has no schema rename (objects are transferred
    // instead), and no USAGE privilege on a schema (the base already reports grants/revokes as unsupported).

    protected override Result<IReadOnlyList<SqlStatement>> RenameSchema(RenameSchema action) =>
        Error($"SQL Server cannot rename schema {action.OldName}: there is no ALTER SCHEMA … RENAME. Create the new schema and transfer its objects instead.");

    protected override Result<IReadOnlyList<SqlStatement>> SetSchemaComment(SetSchemaComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.SchemaName));

    // ── Tables ────────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateTable(CreateTable action)
    {
        var table = action.Table;
        if (table.ExclusionConstraints.Count > 0)
        {
            return Unsupported(action);
        }

        var parts = table.Columns.Select(BuildColumnDef).ToList();

        // Only the primary key is created inline; unique/check constraints, foreign keys and indexes arrive as
        // separate ALTER TABLE / CREATE INDEX actions from the linearizer.
        if (table.PrimaryKey is { } pk)
        {
            parts.Add($"CONSTRAINT {Quote(pk.Name)} PRIMARY KEY ({ColumnList(pk.ColumnNames)})");
        }

        return Statement($"""
            CREATE TABLE {Qualify(action.SchemaName, table.Name)} (
                {string.Join(",\n    ", parts)}
            )
            """);
    }

    protected override Result<IReadOnlyList<SqlStatement>> RenameTable(RenameTable action) =>
        Statement(RenameObject(action.Table, action.NewName));

    protected override Result<IReadOnlyList<SqlStatement>> SetTableComment(SetTableComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Table.Schema), ("TABLE", action.Table.Name));

    // ── Columns ───────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> AddColumn(AddColumn action) =>
        Statement($"ALTER TABLE {Qualify(action.Table)} ADD {BuildColumnDef(action.Column)}");

    // sp_rename takes the object as a quoted string and the new name bare (brackets would become part of the name).
    protected override Result<IReadOnlyList<SqlStatement>> RenameColumn(RenameColumn action) =>
        Statement($"EXEC sys.sp_rename @objname = N'{Lit($"{Qualify(action.Column.Owner)}.{Quote(action.Column.Member)}")}', @newname = N'{Lit(action.NewName.Value)}', @objtype = N'COLUMN'");

    // SQL Server's ALTER COLUMN must restate nullability — omitting it resets the column to NULL. The core supplies
    // the column's final nullability on the action; without it a correct statement cannot be produced.
    protected override Result<IReadOnlyList<SqlStatement>> AlterColumnType(AlterColumnType action) =>
        action.IsNullable is { } isNullable
            ? Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} {TypeSql(action.NewType)}{NullableSql(isNullable)}")
            : Error($"Cannot alter the type of column {action.Column} on SQL Server: the plan does not carry the column's nullability, which ALTER COLUMN must restate.");

    // The counterpart: a nullability change must restate the column's (final) type.
    protected override Result<IReadOnlyList<SqlStatement>> AlterColumnNullability(AlterColumnNullability action) =>
        action.ColumnType is { } columnType
            ? Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} {TypeSql(columnType)}{NullableSql(action.NewNullable)}")
            : Error($"Cannot alter the nullability of column {action.Column} on SQL Server: the plan does not carry the column's type, which ALTER COLUMN requires.");

    protected override Result<IReadOnlyList<SqlStatement>> AlterIdentitySequence(AlterIdentitySequence action) =>
        Error($"SQL Server cannot change the seed or increment of identity column {action.Column} in place; this requires rebuilding the table. Recreate the column or table instead.");

    protected override Result<IReadOnlyList<SqlStatement>> SetColumnGenerated(SetColumnGenerated action) =>
        Error($"SQL Server cannot change the expression of computed column {action.Column} in place; this requires rebuilding the table. Recreate the column or table instead.");

    // A default on SQL Server is a named constraint. Adding is inline (auto-named); dropping needs the name, found
    // via sys.default_constraints since the model tracks defaults by column, not by constraint name.
    protected override Result<IReadOnlyList<SqlStatement>> SetColumnDefault(SetColumnDefault action)
    {
        var statements = new List<SqlStatement>();
        if (action.OldDefault is not null)
        {
            var target = Qualify(action.Column.Owner);
            statements.Add(new SqlStatement($"""
                DECLARE @df sysname = (
                    SELECT dc.name
                    FROM sys.default_constraints dc
                    INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                    WHERE dc.parent_object_id = OBJECT_ID(N'{Lit(target)}') AND c.name = N'{Lit(action.Column.Member.Value)}');
                IF @df IS NOT NULL EXEC('ALTER TABLE {Lit(target)} DROP CONSTRAINT [' + @df + ']');
                """));
        }

        if (action.NewDefault is not null)
        {
            statements.Add(new SqlStatement($"ALTER TABLE {Qualify(action.Column.Owner)} ADD DEFAULT {action.NewDefault} FOR {Quote(action.Column.Member)}"));
        }

        return Statements([.. statements]);
    }

    protected override Result<IReadOnlyList<SqlStatement>> SetColumnComment(SetColumnComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Column.Schema), ("TABLE", action.Column.Object), ("COLUMN", action.Column.Member));

    // ── Constraints (adds/drops use the base ANSI forms) ──────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> SetConstraintComment(SetConstraintComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Constraint.Schema), ("TABLE", action.Constraint.Object), ("CONSTRAINT", action.Constraint.Member));

    // ── Indexes ───────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateIndex(CreateIndex action)
    {
        var idx = action.Index;
        if (idx.Method is not null)
        {
            return Error($"SQL Server indexes have no access method (USING) — index {idx.Name} specifies {idx.Method}.");
        }

        var keys = new List<string>();
        foreach (var col in idx.Columns)
        {
            if (col.Column is null)
            {
                return Error($"SQL Server cannot index the expression {col.Expression} directly; add a computed column and index that instead.");
            }

            if (col.Nulls != IndexNulls.Default)
            {
                return Error($"SQL Server indexes do not support NULLS FIRST / NULLS LAST ordering (index {idx.Name}).");
            }

            var sort = col.Sort switch
            {
                IndexSort.Ascending => " ASC",
                IndexSort.Descending => " DESC",
                _ => "",
            };
            keys.Add($"{Quote(col.Column)}{sort}");
        }

        var unique = idx.IsUnique ? "UNIQUE " : "";
        var include = idx.Include.Count > 0 ? $" INCLUDE ({ColumnList(idx.Include)})" : "";
        var sql = $"CREATE {unique}INDEX {Quote(idx.Name)} ON {Qualify(action.Table)} ({string.Join(", ", keys)}){include}";
        return Statement(idx.Predicate is { } predicate ? $"{sql} WHERE {predicate}" : sql);
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropIndex(DropIndex action) =>
        Statement($"DROP INDEX {Quote(action.Index.Member)} ON {Qualify(action.Index.Owner)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetIndexComment(SetIndexComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Index.Schema), ("TABLE", action.Index.Object), ("INDEX", action.Index.Member));

    // ── Triggers ──────────────────────────────────────────────────────────────

    // CREATE OR ALTER TRIGGER [s].[name] ON [s].[table] {AFTER|INSTEAD OF} {events} AS <body>. SQL Server triggers
    // are statement-level, fire only AFTER or INSTEAD OF, carry no WHEN clause and run an inline body — facets of
    // the model that don't map (BEFORE, row-level, WHEN, TRUNCATE, UPDATE OF, a function indirection) are rejected.
    protected override Result<IReadOnlyList<SqlStatement>> CreateTrigger(CreateTrigger action)
    {
        var trigger = action.Trigger;
        if (trigger.Body is not { } body)
        {
            return Error($"SQL Server triggers run an inline body, but trigger {trigger.Name} has none (it calls a function). Declare it with an AS $$ … $$ body instead.");
        }

        if (trigger.Timing == TriggerTiming.Before)
        {
            return Error($"SQL Server does not support BEFORE triggers (trigger {trigger.Name}); only AFTER and INSTEAD OF are available.");
        }

        if (trigger.Level == TriggerLevel.Row)
        {
            return Error($"SQL Server does not support row-level (FOR EACH ROW) triggers (trigger {trigger.Name}); triggers fire once per statement — use the inserted/deleted tables.");
        }

        if (trigger.When is not null)
        {
            return Error($"SQL Server does not support a trigger WHEN condition (trigger {trigger.Name}); put the guard inside the body, e.g. IF UPDATE(column).");
        }

        if (trigger.Events.HasFlag(TriggerEvent.Truncate))
        {
            return Error($"SQL Server does not support TRUNCATE triggers (trigger {trigger.Name}).");
        }

        if (trigger.UpdateOfColumns.Count > 0)
        {
            return Error($"SQL Server does not support UPDATE OF (columns) on a trigger (trigger {trigger.Name}); use IF UPDATE(column) inside the body.");
        }

        var timing = trigger.Timing == TriggerTiming.InsteadOf ? "INSTEAD OF" : "AFTER";
        return Statement($"CREATE OR ALTER TRIGGER {Qualify(action.Table.Schema, trigger.Name)} ON {Qualify(action.Table)} {timing} {TriggerEventsSql(trigger.Events)} AS {body}");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropTrigger(DropTrigger action) =>
        Statement($"DROP TRIGGER {Qualify(action.Trigger.Schema, action.Trigger.Member)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetTriggerComment(SetTriggerComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Trigger.Schema), ("TABLE", action.Trigger.Object), ("TRIGGER", action.Trigger.Member));

    // ── Views (CREATE OR ALTER replaces both an add and a body change in place) ──

    protected override Result<IReadOnlyList<SqlStatement>> CreateView(CreateView action) =>
        action.View.IsMaterialized
            ? Unsupported(action)
            : Statement($"CREATE OR ALTER VIEW {Qualify(action.SchemaName, action.View.Name)} AS {action.View.Body}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameView(RenameView action) =>
        action.IsMaterialized
            ? Unsupported(action)
            : Statement(RenameObject(action.View, action.NewName));

    protected override Result<IReadOnlyList<SqlStatement>> SetViewComment(SetViewComment action) =>
        action.IsMaterialized
            ? Unsupported(action)
            : ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.View.Schema), ("VIEW", action.View.Name));

    // ── Sequences ─────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateSequence(CreateSequence action)
    {
        var o = action.Sequence.Options;
        var parts = new List<string>();
        if (o.DataType is { } type)
        {
            parts.Add($"AS {TypeSql(type)}");
        }

        if (o.StartWith is { } start)
        {
            parts.Add($"START WITH {start}");
        }

        if (o.IncrementBy is { } increment)
        {
            parts.Add($"INCREMENT BY {increment}");
        }

        parts.Add(o.MinValue is { } min ? $"MINVALUE {min}" : "NO MINVALUE");
        parts.Add(o.MaxValue is { } max ? $"MAXVALUE {max}" : "NO MAXVALUE");
        parts.Add(o.Cache is { } cache ? $"CACHE {cache}" : "NO CACHE");
        parts.Add(o.Cycle ? "CYCLE" : "NO CYCLE");

        return Statement($"CREATE SEQUENCE {Qualify(action.SchemaName, action.Sequence.Name)} {string.Join(" ", parts)}");
    }

    // One clause per option that differs. SQL Server's ALTER SEQUENCE cannot change the data type, so a type change
    // is rejected; a start change becomes RESTART WITH (or a bare RESTART back to the declared start).
    protected override Result<IReadOnlyList<SqlStatement>> AlterSequence(AlterSequence action)
    {
        var (old, @new) = (action.OldOptions, action.NewOptions);
        if (old.DataType != @new.DataType)
        {
            return Error($"SQL Server cannot change the data type of sequence {action.Sequence} with ALTER SEQUENCE; drop and recreate the sequence instead.");
        }

        var parts = new List<string>();
        if (old.IncrementBy != @new.IncrementBy)
        {
            parts.Add($"INCREMENT BY {@new.IncrementBy ?? 1}");
        }

        if (old.MinValue != @new.MinValue)
        {
            parts.Add(@new.MinValue is { } min ? $"MINVALUE {min}" : "NO MINVALUE");
        }

        if (old.MaxValue != @new.MaxValue)
        {
            parts.Add(@new.MaxValue is { } max ? $"MAXVALUE {max}" : "NO MAXVALUE");
        }

        if (old.Cache != @new.Cache)
        {
            parts.Add(@new.Cache is { } cache ? $"CACHE {cache}" : "NO CACHE");
        }

        if (old.Cycle != @new.Cycle)
        {
            parts.Add(@new.Cycle ? "CYCLE" : "NO CYCLE");
        }

        if (old.StartWith != @new.StartWith)
        {
            parts.Add(@new.StartWith is { } start ? $"RESTART WITH {start}" : "RESTART");
        }

        return Statement($"ALTER SEQUENCE {Qualify(action.Sequence)} {string.Join(" ", parts)}");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropSequence(DropSequence action) =>
        Statement($"DROP SEQUENCE {Qualify(action.Sequence)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameSequence(RenameSequence action) =>
        Statement(RenameObject(action.Sequence, action.NewName));

    protected override Result<IReadOnlyList<SqlStatement>> SetSequenceComment(SetSequenceComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Sequence.Schema), ("SEQUENCE", action.Sequence.Name));

    // ── Routines (CREATE OR ALTER keeps the object identity, so comments survive a recreate) ──

    protected override Result<IReadOnlyList<SqlStatement>> CreateRoutine(CreateRoutine action) =>
        CreateOrAlterRoutine(action.SchemaName, action.Routine);

    protected override Result<IReadOnlyList<SqlStatement>> RecreateRoutine(RecreateRoutine action) =>
        CreateOrAlterRoutine(action.SchemaName, action.Routine);

    protected override Result<IReadOnlyList<SqlStatement>> DropRoutine(DropRoutine action) =>
        Statement($"DROP {RoutineKeyword(action.Kind)} {Qualify(action.Routine)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameRoutine(RenameRoutine action) =>
        Statement(RenameObject(action.Routine, action.NewName));

    protected override Result<IReadOnlyList<SqlStatement>> SetRoutineComment(SetRoutineComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Routine.Schema), (RoutineKeyword(action.Kind), action.Routine.Name));

    // CREATE OR ALTER replaces a function or procedure in place (SQL Server 2016 SP1+), keeping the object identity
    // so that extended-property comments survive — which is why a signature-changing recreate needs no re-comment.
    private Result<IReadOnlyList<SqlStatement>> CreateOrAlterRoutine(SqlIdentifier schemaName, Routine routine) =>
        Statement($"CREATE OR ALTER {RoutineKeyword(routine.RoutineKind)} {Qualify(schemaName, routine.Name)}({routine.Arguments}) {routine.Definition}");

    // ── Building blocks ───────────────────────────────────────────────────────

    private string BuildColumnDef(Column col)
    {
        // A computed (generated) column states no type — only its expression, persisted to storage.
        if (col.GeneratedExpression is { } generated)
        {
            return $"{Quote(col.Name)} AS ({generated}) PERSISTED";
        }

        var identity = col.IsIdentity ? BuildIdentityClause(col.IdentityOptions) : "";
        // Identity and DEFAULT are mutually exclusive on SQL Server; the core's structural policy keeps a default
        // off an identity column, so this only adds a default to a plain column.
        var def = col is { DefaultExpression: { } d, IsIdentity: false } ? $" DEFAULT {d}" : "";
        return $"{Quote(col.Name)} {TypeSql(col.Type)}{identity}{NullableSql(col.IsNullable)}{def}";
    }

    // SQL Server identity uses a (seed, increment) pair; there is no minimum-value concept, so
    // IdentityOptions.MinValue is not expressible and is ignored. Absent options default to IDENTITY(1, 1).
    private static string BuildIdentityClause(IdentityOptions? options) =>
        $" IDENTITY({options?.StartWith ?? 1}, {options?.IncrementBy ?? 1})";

    // MS_Description is added, updated or dropped depending on whether the comment is appearing, changing or going
    // away — which the Old/New pair on the action expresses directly. Levels are 0..2 (schema, object, sub-object).
    private Result<IReadOnlyList<SqlStatement>> ExtendedProperty(string? oldComment, string? newComment, params (string Type, SqlIdentifier Name)[] levels)
    {
        var procedure = (oldComment, newComment) switch
        {
            (null, not null) => "sp_addextendedproperty",
            (not null, null) => "sp_dropextendedproperty",
            _ => "sp_updateextendedproperty",
        };

        var sb = new StringBuilder($"EXEC sys.{procedure} @name = N'{DescriptionProperty}'");
        if (newComment is not null)
        {
            sb.Append($", @value = N'{Lit(newComment)}'");
        }

        for (var i = 0; i < levels.Length; i++)
        {
            sb.Append($", @level{i}type = N'{levels[i].Type}', @level{i}name = N'{Lit(levels[i].Name.Value)}'");
        }

        return Statement(sb.ToString());
    }

    private string TypeSql(SqlType type) => type.Name.Value.ToLowerInvariant() switch
    {
        "boolean" => "bit",
        "tinyint" => "tinyint",
        "smallint" => "smallint",
        "int" => "int",
        "bigint" => "bigint",
        "float" => "real",
        "double" => "float",
        "decimal" => $"decimal({type.Precision}, {type.Scale})",
        "char" => $"char({type.Length})",
        "nchar" => $"nchar({type.Length})",
        "varchar" => type.Length is { } n ? $"varchar({n})" : "varchar(max)",
        "nvarchar" => type.Length is { } n ? $"nvarchar({n})" : "nvarchar(max)",
        "text" => "varchar(max)",
        "date" => "date",
        "time" => "time",
        "datetime" => "datetime2",
        "datetimeoffset" => "datetimeoffset",
        "guid" => "uniqueidentifier",
        "binary" => $"binary({type.Length})",
        "varbinary" => type.Length is { } n ? $"varbinary({n})" : "varbinary(max)",
        // Any other name is a SQL Server-specific or user-defined type (e.g. money, xml, hierarchyid); emit it
        // verbatim, qualified by its owning schema when it has one.
        _ => type.Schema is { } schema ? $"{Quote(schema)}.{Quote(type.Name)}" : type.Name.Value,
    };

    // sp_rename takes the object as a quoted string and the new name bare (brackets would become part of the name).
    private string RenameObject(ObjectAddress address, SqlIdentifier newName) =>
        $"EXEC sys.sp_rename @objname = N'{Lit(Qualify(address))}', @newname = N'{Lit(newName.Value)}', @objtype = N'OBJECT'";

    private static string NullableSql(bool isNullable) => isNullable ? " NULL" : " NOT NULL";

    // Doubles single quotes for embedding inside an N'...' string literal.
    private static string Lit(string value) => value.Replace("'", "''");

    private static string RoutineKeyword(RoutineKind kind) => kind == RoutineKind.Procedure ? "PROCEDURE" : "FUNCTION";

    private static string TriggerEventsSql(TriggerEvent events)
    {
        var parts = new List<string>(3);
        if (events.HasFlag(TriggerEvent.Insert))
        {
            parts.Add("INSERT");
        }

        if (events.HasFlag(TriggerEvent.Update))
        {
            parts.Add("UPDATE");
        }

        if (events.HasFlag(TriggerEvent.Delete))
        {
            parts.Add("DELETE");
        }

        return string.Join(", ", parts);
    }
}
