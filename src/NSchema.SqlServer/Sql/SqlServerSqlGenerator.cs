using System.Text;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.CompositeTypes;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Domains;
using NSchema.Plan.Model.Enums;
using NSchema.Plan.Model.Extensions;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Sequence;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Indexes;
using NSchema.Schema.Model.Routines;
using NSchema.Schema.Model.Tables;
using NSchema.Sql;
using NSchema.Sql.Model;

namespace NSchema.SqlServer.Sql;

/// <summary>
/// Translates an NSchema <see cref="MigrationPlan"/> into SQL Server (T-SQL) DDL.
/// </summary>
/// <remarks>
/// SQL Server's surface differs from PostgreSQL's in a few places that shape this generator:
/// <list type="bullet">
/// <item><b>ALTER COLUMN is monolithic.</b> T-SQL restates a column's full type and nullability in one statement, so
/// when a plan changes both, the type action emits the combined <c>ALTER COLUMN</c> and the paired nullability action
/// is folded away. Each action carries the column's final type/nullability (supplied by the core), so a lone type or
/// nullability change still produces a complete statement.</item>
/// <item><b>Defaults are named constraints.</b> A default is added inline or via <c>ADD DEFAULT … FOR</c>; dropping one
/// requires finding its auto-generated constraint name, which is done with a small dynamic-SQL block.</item>
/// <item><b>Renames go through <c>sp_rename</c></b> for tables, columns, views, routines and sequences.</item>
/// <item><b>No equivalent (throws <see cref="NotSupportedException"/>):</b> triggers (the model calls a function),
/// enums, domains, composite types, extensions, exclusion constraints, schema rename, materialized views, and in-place
/// changes to a computed-column expression or an identity's seed/increment (SQL Server requires a table rebuild).</item>
/// </list>
/// </remarks>
internal sealed class SqlServerSqlGenerator : ISqlGenerator
{
    private const string DescriptionProperty = "MS_Description";

    /// <summary>Records which columns have a type and/or nullability change in the plan, so the two can be folded.</summary>
    private sealed record ColumnAlterContext(
        HashSet<(string Schema, string Table, string Column)> TypeChanges,
        HashSet<(string Schema, string Table, string Column)> NullabilityChanges);

    public SqlPlan Generate(MigrationPlan plan)
    {
        var preDeploymentStatements = plan.PreDeploymentScripts.Select(s => new SqlStatement(s.Sql, s.RunOutsideTransaction));
        var postDeploymentStatements = plan.PostDeploymentScripts.Select(s => new SqlStatement(s.Sql, s.RunOutsideTransaction));

        // A column whose type and nullability both change arrives as two actions; SQL Server restates the whole
        // column in one ALTER COLUMN, so the type action emits the combined statement and the nullability one is folded.
        var context = new ColumnAlterContext(
            plan.Actions.OfType<AlterColumnType>().Select(a => (a.SchemaName, a.TableName, a.ColumnName)).ToHashSet(),
            plan.Actions.OfType<AlterColumnNullability>().Select(a => (a.SchemaName, a.TableName, a.ColumnName)).ToHashSet());

        var statements = plan.Actions.SelectMany(action => GenerateStatements(action, context)).ToList();
        List<SqlStatement> allStatements = [.. preDeploymentStatements, .. statements, .. postDeploymentStatements];
        return new SqlPlan(allStatements);
    }

    // ── SQL generation ──────────────────────────────────────────────────────────

    private static IEnumerable<SqlStatement> GenerateStatements(MigrationAction action, ColumnAlterContext context) => action switch
    {
        // Changing a column's default is a drop (find the auto-named constraint dynamically) followed by an add; either
        // half may be absent. The unchanged half of a type/nullability pair is folded — see AlterColumn*.
        SetColumnDefault x => BuildSetColumnDefault(x),
        AlterColumnType x => [new SqlStatement(BuildAlterColumnType(x))],
        AlterColumnNullability x => BuildAlterColumnNullability(x, context),
        _ => [new SqlStatement(GenerateSql(action))],
    };

    private static string GenerateSql(MigrationAction action) => action switch
    {
        // ── Schemas ───────────────────────────────────────────────────────────────
        // CREATE SCHEMA must be the first statement in its batch; the executor runs each statement as its own batch,
        // so a bare CREATE SCHEMA is valid. SQL Server has no schema rename (objects are transferred instead).
        CreateSchema x => $"CREATE SCHEMA {Id(x.SchemaName)}",
        DropSchema x => $"DROP SCHEMA {Id(x.SchemaName)}",
        RenameSchema => throw Unsupported("renaming a schema (SQL Server has no ALTER SCHEMA … RENAME; create the new schema and transfer objects)"),

        // ── Tables ──────────────────────────────────────────────────────────────────
        CreateTable x => BuildCreateTable(x),
        DropTable x => $"DROP TABLE {Qualify(x.SchemaName, x.TableName)}",
        RenameTable x => RenameObject(x.SchemaName, x.OldName, x.NewName),

        // ── Columns (ADD / DROP / RENAME; type & nullability handled in GenerateStatements) ──
        AddColumn x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} ADD {BuildColumnDef(x.Column)}",
        DropColumn x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} DROP COLUMN {Id(x.ColumnName)}",
        RenameColumn x => $"EXEC sys.sp_rename @objname = N'{Lit($"{Qualify(x.SchemaName, x.TableName)}.{Id(x.OldName)}")}', @newname = N'{Lit(x.NewName)}', @objtype = N'COLUMN'",
        SetColumnGenerated => throw RequiresRebuild("change a computed column's expression"),
        AlterIdentitySequence => throw RequiresRebuild("change an identity column's seed or increment"),

        // ── Keys and constraints ─────────────────────────────────────────────────────
        AddPrimaryKey x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} ADD CONSTRAINT {Id(x.PrimaryKey.Name)} PRIMARY KEY ({ColList(x.PrimaryKey.ColumnNames)})",
        DropPrimaryKey x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} DROP CONSTRAINT {Id(x.PrimaryKeyName)}",
        AddForeignKey x => BuildAddForeignKey(x),
        DropForeignKey x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} DROP CONSTRAINT {Id(x.ForeignKeyName)}",
        AddUniqueConstraint x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} ADD CONSTRAINT {Id(x.UniqueConstraint.Name)} UNIQUE ({ColList(x.UniqueConstraint.ColumnNames)})",
        DropUniqueConstraint x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} DROP CONSTRAINT {Id(x.ConstraintName)}",
        AddCheckConstraint x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} ADD CONSTRAINT {Id(x.CheckConstraint.Name)} CHECK ({x.CheckConstraint.Expression})",
        DropCheckConstraint x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} DROP CONSTRAINT {Id(x.ConstraintName)}",
        AddExclusionConstraint or DropExclusionConstraint => throw Unsupported("exclusion constraints"),

        // ── Indexes ───────────────────────────────────────────────────────────────────
        CreateIndex x => BuildCreateIndex(x),
        DropIndex x => $"DROP INDEX {Id(x.IndexName)} ON {Qualify(x.SchemaName, x.TableName)}",

        // ── Views (CREATE OR ALTER replaces both an add and a body change in place) ──────
        CreateView { View.IsMaterialized: true } => throw Unsupported("materialized (indexed) views"),
        CreateView x => $"CREATE OR ALTER VIEW {Qualify(x.SchemaName, x.View.Name)} AS {x.View.Body}",
        DropView { IsMaterialized: true } => throw Unsupported("materialized (indexed) views"),
        DropView x => $"DROP VIEW {Qualify(x.SchemaName, x.ViewName)}",
        RenameView { IsMaterialized: true } => throw Unsupported("materialized (indexed) views"),
        RenameView x => RenameObject(x.SchemaName, x.OldName, x.NewName),

        // ── Sequences ─────────────────────────────────────────────────────────────────
        CreateSequence x => BuildCreateSequence(x),
        DropSequence x => $"DROP SEQUENCE {Qualify(x.SchemaName, x.SequenceName)}",
        RenameSequence x => RenameObject(x.SchemaName, x.OldName, x.NewName),
        AlterSequence x => BuildAlterSequence(x),

        // ── Routines (CREATE OR ALTER keeps the object identity, so comments survive a recreate) ──
        CreateRoutine x => BuildCreateOrAlterRoutine(x.SchemaName, x.Routine),
        RecreateRoutine x => BuildCreateOrAlterRoutine(x.SchemaName, x.Routine),
        DropRoutine x => $"DROP {RoutineKeyword(x.Kind)} {Qualify(x.SchemaName, x.RoutineName)}",
        RenameRoutine x => RenameObject(x.SchemaName, x.OldName, x.NewName),

        // ── Comments (extended properties) ──────────────────────────────────────────────
        SetSchemaComment x => ExtendedProperty(x.OldComment, x.NewComment, ("SCHEMA", x.SchemaName)),
        SetTableComment x => ExtendedProperty(x.OldComment, x.NewComment, ("SCHEMA", x.SchemaName), ("TABLE", x.TableName)),
        SetColumnComment x => ExtendedProperty(x.OldComment, x.NewComment, ("SCHEMA", x.SchemaName), ("TABLE", x.TableName), ("COLUMN", x.ColumnName)),
        SetIndexComment x => ExtendedProperty(x.OldComment, x.NewComment, ("SCHEMA", x.SchemaName), ("TABLE", x.TableName), ("INDEX", x.IndexName)),
        SetConstraintComment x => ExtendedProperty(x.OldComment, x.NewComment, ("SCHEMA", x.SchemaName), ("TABLE", x.TableName), ("CONSTRAINT", x.ConstraintName)),
        SetViewComment { IsMaterialized: true } => throw Unsupported("materialized (indexed) views"),
        SetViewComment x => ExtendedProperty(x.OldComment, x.NewComment, ("SCHEMA", x.SchemaName), ("VIEW", x.ViewName)),
        SetSequenceComment x => ExtendedProperty(x.OldComment, x.NewComment, ("SCHEMA", x.SchemaName), ("SEQUENCE", x.SequenceName)),
        SetRoutineComment x => ExtendedProperty(x.OldComment, x.NewComment, ("SCHEMA", x.SchemaName), (RoutineKeyword(x.Kind), x.RoutineName)),

        // ── Grants (table privileges; SQL Server has no schema-level USAGE) ──────────────
        GrantTablePrivileges x => $"GRANT {PrivilegeList(x.Privileges)} ON {Qualify(x.SchemaName, x.TableName)} TO {Id(x.Role)}",
        RevokeTablePrivileges x => $"REVOKE {PrivilegeList(x.Privileges)} ON {Qualify(x.SchemaName, x.TableName)} FROM {Id(x.Role)}",
        GrantSchemaUsage or RevokeSchemaUsage => throw Unsupported("schema USAGE grants (SQL Server has no USAGE privilege on a schema)"),

        // ── Features with no SQL Server equivalent in this model ─────────────────────────
        CreateTrigger or DropTrigger or SetTriggerComment => throw Unsupported("triggers (NSchema models a trigger as calling a function, which SQL Server's trigger bodies are not)"),
        CreateEnum or DropEnum or RenameEnum or AddEnumValue or SetEnumComment => throw Unsupported("enum types"),
        CreateDomain or DropDomain or RenameDomain or RecreateDomain or AlterDomainDefault or AlterDomainNotNull or AddDomainCheck or DropDomainCheck or SetDomainComment => throw Unsupported("domains"),
        CreateCompositeType or DropCompositeType or RenameCompositeType or AddCompositeField or DropCompositeField or AlterCompositeFieldType or SetCompositeTypeComment => throw Unsupported("composite types"),
        CreateExtension or DropExtension or AlterExtension or SetExtensionComment => throw Unsupported("extensions"),

        _ => throw new ArgumentOutOfRangeException(nameof(action), $"Unhandled action type: {action.GetType().Name}"),
    };

    // ── Tables ────────────────────────────────────────────────────────────────────

    private static string BuildCreateTable(CreateTable x)
    {
        var table = x.Table;
        if (table.ExclusionConstraints.Count > 0)
        {
            throw Unsupported("exclusion constraints");
        }

        var parts = table.Columns.Select(BuildColumnDef).ToList();

        // Only the primary key is created inline; unique/check constraints, foreign keys and indexes arrive as
        // separate ALTER TABLE / CREATE INDEX actions from the linearizer (matching the Postgres provider).
        if (table.PrimaryKey is { } pk)
        {
            parts.Add($"CONSTRAINT {Id(pk.Name)} PRIMARY KEY ({ColList(pk.ColumnNames)})");
        }

        return $"""
            CREATE TABLE {Qualify(x.SchemaName, table.Name)} (
                {string.Join(",\n    ", parts)}
            )
            """;
    }

    private static string BuildColumnDef(Column col)
    {
        // A computed (generated) column states no type — only its expression, persisted to storage.
        if (col.GeneratedExpression is { } generated)
        {
            return $"{Id(col.Name)} AS ({generated}) PERSISTED";
        }

        var type = ToSqlServerType(col.Type);
        var identity = col.IsIdentity ? BuildIdentityClause(col.IdentityOptions) : "";
        var nullable = col.IsNullable ? " NULL" : " NOT NULL";
        // Identity and DEFAULT are mutually exclusive on SQL Server; the core's structural policy keeps a default off
        // an identity column, so this only adds a default to a plain column.
        var def = col is { DefaultExpression: { } d, IsIdentity: false } ? $" DEFAULT {d}" : "";
        return $"{Id(col.Name)} {type}{identity}{nullable}{def}";
    }

    // SQL Server identity uses a (seed, increment) pair; there is no minimum-value concept, so IdentityOptions.MinValue
    // is not expressible and is ignored. Absent options default to IDENTITY(1, 1).
    private static string BuildIdentityClause(NSchema.Schema.Model.Columns.IdentityOptions? options) =>
        $" IDENTITY({options?.StartWith ?? 1}, {options?.IncrementBy ?? 1})";

    private static string BuildAddForeignKey(AddForeignKey x)
    {
        var fk = x.ForeignKey;
        var onDelete = ToReferentialAction(fk.OnDelete);
        var onUpdate = ToReferentialAction(fk.OnUpdate);
        return $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} ADD CONSTRAINT {Id(fk.Name)} FOREIGN KEY ({ColList(fk.ColumnNames)}) REFERENCES {Qualify(fk.ReferencedSchema, fk.ReferencedTable)} ({ColList(fk.ReferencedColumnNames)}) ON DELETE {onDelete} ON UPDATE {onUpdate}";
    }

    // ── Column type / nullability ───────────────────────────────────────────────────

    private static string BuildAlterColumnType(AlterColumnType x)
    {
        // SQL Server's ALTER COLUMN must restate nullability — omitting it resets the column to NULL. The core supplies
        // the column's final nullability on the action; without it a correct statement cannot be produced.
        if (x.IsNullable is not { } isNullable)
        {
            throw new NotSupportedException(
                $"Cannot alter the type of column {Qualify(x.SchemaName, x.TableName)}.{Id(x.ColumnName)} on SQL Server: the plan did not carry the column's nullability, which ALTER COLUMN must restate. Regenerate the plan with NSchema.Core 3.1.0 or newer.");
        }

        return $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} ALTER COLUMN {Id(x.ColumnName)} {ToSqlServerType(x.NewType)}{Nullable(isNullable)}";
    }

    private static IEnumerable<SqlStatement> BuildAlterColumnNullability(AlterColumnNullability x, ColumnAlterContext context)
    {
        // When the same column's type also changes, the type action's ALTER COLUMN already restates the final
        // nullability (both actions carry the column's final state), so this one is folded away.
        if (context.TypeChanges.Contains((x.SchemaName, x.TableName, x.ColumnName)))
        {
            return [];
        }

        if (x.ColumnType is not { } columnType)
        {
            throw new NotSupportedException(
                $"Cannot alter the nullability of column {Qualify(x.SchemaName, x.TableName)}.{Id(x.ColumnName)} on SQL Server: the plan did not carry the column's type, which ALTER COLUMN requires. Regenerate the plan with NSchema.Core 3.1.0 or newer.");
        }

        return [new SqlStatement($"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} ALTER COLUMN {Id(x.ColumnName)} {ToSqlServerType(columnType)}{Nullable(x.NewNullable)}")];
    }

    // A default on SQL Server is a named constraint. Adding is inline (auto-named); dropping needs the name, found via
    // sys.default_constraints since the model tracks defaults by column, not by constraint name.
    private static IEnumerable<SqlStatement> BuildSetColumnDefault(SetColumnDefault x)
    {
        var statements = new List<SqlStatement>();
        if (x.OldDefault is not null)
        {
            var target = Qualify(x.SchemaName, x.TableName);
            statements.Add(new SqlStatement($"""
                DECLARE @df sysname = (
                    SELECT dc.name
                    FROM sys.default_constraints dc
                    INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                    WHERE dc.parent_object_id = OBJECT_ID(N'{Lit(target)}') AND c.name = N'{Lit(x.ColumnName)}');
                IF @df IS NOT NULL EXEC('ALTER TABLE {Lit(target)} DROP CONSTRAINT [' + @df + ']');
                """));
        }

        if (x.NewDefault is not null)
        {
            statements.Add(new SqlStatement($"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} ADD DEFAULT {x.NewDefault} FOR {Id(x.ColumnName)}"));
        }

        return statements;
    }

    // ── Indexes ───────────────────────────────────────────────────────────────────

    private static string BuildCreateIndex(CreateIndex x)
    {
        var idx = x.Index;
        if (idx.Method is not null)
        {
            throw new NotSupportedException($"SQL Server indexes have no access method (USING) — index '{idx.Name}' specifies '{idx.Method}'.");
        }

        var keys = string.Join(", ", idx.Columns.Select(IndexKeyText));
        var unique = idx.IsUnique ? "UNIQUE " : "";
        var include = idx.Include.Count > 0 ? $" INCLUDE ({ColList(idx.Include)})" : "";
        var sql = $"CREATE {unique}INDEX {Id(idx.Name)} ON {Qualify(x.SchemaName, x.TableName)} ({keys}){include}";
        return idx.Predicate is { } predicate ? $"{sql} WHERE {predicate}" : sql;
    }

    // A plain column key is bracketed; an expression key is not valid in a SQL Server index (only computed columns are
    // indexable), so an expression key is rejected. ASC/DESC is emitted only when explicit; SQL Server has no NULLS
    // FIRST/LAST ordering, so a non-default null ordering is rejected.
    private static string IndexKeyText(IndexColumn col)
    {
        if (col.IsExpression)
        {
            throw new NotSupportedException($"SQL Server cannot index the expression '{col.Expression}' directly; add a computed column and index that instead.");
        }

        if (col.Nulls != IndexNulls.Default)
        {
            throw new NotSupportedException("SQL Server indexes do not support NULLS FIRST / NULLS LAST ordering.");
        }

        var sort = col.Sort switch
        {
            IndexSort.Ascending => " ASC",
            IndexSort.Descending => " DESC",
            _ => "",
        };
        return $"{Id(col.Expression)}{sort}";
    }

    // ── Sequences ───────────────────────────────────────────────────────────────────

    private static string BuildCreateSequence(CreateSequence x)
    {
        var o = x.Sequence.Options;
        var parts = new List<string>();
        if (o.DataType is { } type)
        {
            parts.Add($"AS {ToSqlServerType(type)}");
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

        return $"CREATE SEQUENCE {Qualify(x.SchemaName, x.Sequence.Name)} {string.Join(" ", parts)}";
    }

    // One clause per option that differs. SQL Server's ALTER SEQUENCE cannot change the data type, so a type change is
    // rejected; a start change becomes RESTART WITH (or a bare RESTART back to the declared start).
    private static string BuildAlterSequence(AlterSequence x)
    {
        var (old, @new) = (x.OldOptions, x.NewOptions);
        if (old.DataType != @new.DataType)
        {
            throw new NotSupportedException("SQL Server cannot change a sequence's data type with ALTER SEQUENCE; drop and recreate the sequence instead.");
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

        return $"ALTER SEQUENCE {Qualify(x.SchemaName, x.SequenceName)} {string.Join(" ", parts)}";
    }

    // ── Routines ────────────────────────────────────────────────────────────────────

    // CREATE OR ALTER replaces a function or procedure in place (SQL Server 2016 SP1+), keeping the object identity so
    // that extended-property comments survive — which is why a signature-changing RecreateRoutine needs no re-comment.
    private static string BuildCreateOrAlterRoutine(string schemaName, Routine routine) =>
        $"CREATE OR ALTER {RoutineKeyword(routine.Kind)} {Qualify(schemaName, routine.Name)}({routine.Arguments}) {routine.Definition}";

    // ── Extended properties (comments) ──────────────────────────────────────────────

    // MS_Description is added, updated or dropped depending on whether the comment is appearing, changing or going away
    // — which the Old/New pair on the action expresses directly. Levels are 0..2 (schema, then object, then sub-object).
    private static string ExtendedProperty(string? oldComment, string? newComment, params (string Type, string Name)[] levels)
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
            sb.Append($", @level{i}type = N'{levels[i].Type}', @level{i}name = N'{Lit(levels[i].Name)}'");
        }

        return sb.ToString();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static string ToSqlServerType(SqlType type) => type.Name switch
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
        // Any other name is a SQL Server-specific or user-defined type (e.g. money, xml, hierarchyid); emit it verbatim.
        _ => type.Name,
    };

    // sp_rename takes the object as a quoted string and the new name bare (brackets would become part of the name).
    private static string RenameObject(string schema, string oldName, string newName) =>
        $"EXEC sys.sp_rename @objname = N'{Lit(Qualify(schema, oldName))}', @newname = N'{Lit(newName)}', @objtype = N'OBJECT'";

    private static string Nullable(bool isNullable) => isNullable ? " NULL" : " NOT NULL";

    private static string Qualify(string schema, string name) => $"{Id(schema)}.{Id(name)}";

    // A bracket-quoted identifier; a literal ']' inside a name is doubled.
    private static string Id(string name) => $"[{name.Replace("]", "]]")}]";

    private static string ColList(IReadOnlyList<string> columns) => string.Join(", ", columns.Select(Id));

    // Doubles single quotes for embedding inside an N'...' string literal.
    private static string Lit(string value) => value.Replace("'", "''");

    private static string RoutineKeyword(RoutineKind kind) => kind == RoutineKind.Procedure ? "PROCEDURE" : "FUNCTION";

    private static string ToReferentialAction(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };

    private static string PrivilegeList(TablePrivilege privileges)
    {
        var parts = new List<string>();
        if (privileges.HasFlag(TablePrivilege.Select))
        {
            parts.Add("SELECT");
        }

        if (privileges.HasFlag(TablePrivilege.Insert))
        {
            parts.Add("INSERT");
        }

        if (privileges.HasFlag(TablePrivilege.Update))
        {
            parts.Add("UPDATE");
        }

        if (privileges.HasFlag(TablePrivilege.Delete))
        {
            parts.Add("DELETE");
        }

        // SQL Server deprecated GRANT ALL, so the explicit privilege list is always emitted.
        return parts.Count > 0 ? string.Join(", ", parts) : "SELECT, INSERT, UPDATE, DELETE";
    }

    private static NotSupportedException Unsupported(string feature) =>
        new($"SQL Server (NSchema.SqlServer) does not support {feature}.");

    private static NotSupportedException RequiresRebuild(string operation) =>
        new($"SQL Server cannot {operation} in place; this requires rebuilding the table, which NSchema.SqlServer does not support. Recreate the column or table instead.");
}
