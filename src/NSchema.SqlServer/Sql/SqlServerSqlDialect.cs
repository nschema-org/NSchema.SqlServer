using System.Text;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Domains;
using NSchema.Model.Indexes;
using NSchema.Model.Routines;
using NSchema.Model.Services;
using NSchema.Model.Triggers;
using NSchema.Model.Views;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Columns;
using NSchema.Plan.Domain.Constraints;
using NSchema.Plan.Domain.Domains;
using NSchema.Plan.Domain.Indexes;
using NSchema.Plan.Domain.Routines;
using NSchema.Plan.Domain.Schemas;
using NSchema.Plan.Domain.Sequences;
using NSchema.Plan.Domain.Tables;
using NSchema.Plan.Domain.Triggers;
using NSchema.Plan.Domain.Views;
using NSchema.Plan.Domain.XmlSchemaCollections;
using NSchema.Plan.Plugins;

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

    /// <inheritdoc />
    /// <remarks>
    /// A SQL Server table is stored as its clustered index (a heap only when it has none), so clustering is a
    /// property of the index and it is written on the declaration.
    /// </remarks>
    public override bool SupportsClustering => true;

    /// <summary>SQL Server computes a column on read unless it is declared PERSISTED.</summary>
    public override bool SupportsVirtualGeneratedColumns => true;

    /// <summary>ROWGUIDCOL is SQL Server's own; no other engine has it.</summary>
    public override bool SupportsRowGuidColumns => true;

    /// <summary>SQL Server makes every column default a named constraint.</summary>
    public override bool SupportsNamedDefaults => true;

    /// <summary>NOT FOR REPLICATION is SQL Server's own.</summary>
    public override bool SupportsNotForReplication => true;

    /// <summary>A bracket-quoted identifier; a literal ']' inside a name is doubled.</summary>
    protected override string Quote(SqlIdentifier identifier) => $"[{identifier.Value.Replace("]", "]]")}]";

    /// <summary>A facet of the declaration that SQL Server has no way to express.</summary>
    private static Result<IReadOnlyList<SqlStatement>> Unsupported(FormattedText message) => Error("unsupported", message);

    /// <summary>A change SQL Server cannot make in place, so the object has to be rebuilt instead.</summary>
    private static Result<IReadOnlyList<SqlStatement>> RequiresRecreate(FormattedText message) => Error("requires-recreate", message);

    /// <summary>A failed rendering with a SQL Server-specific explanation.</summary>
    private static Result<IReadOnlyList<SqlStatement>> Error(DiagnosticCode code, FormattedText message) =>
        Result.Failure<IReadOnlyList<SqlStatement>>(Diagnostic.Error(Source, code, message));

    /// <summary>A facet SQL Server cannot express, reported by a helper that renders a fragment rather than an action.</summary>
    private static Result<string> UnsupportedFragment(FormattedText message) =>
        Result.Failure<string>(Diagnostic.Error(Source, "unsupported", message));

    // ── Schemas ───────────────────────────────────────────────────────────────
    // CREATE SCHEMA / DROP SCHEMA use the base forms. SQL Server has no schema rename (objects are transferred
    // instead), and no USAGE privilege on a schema (the base already reports grants/revokes as unsupported).

    protected override Result<IReadOnlyList<SqlStatement>> RenameSchema(RenameSchema action) =>
        RequiresRecreate($"SQL Server cannot rename schema {action.OldName}: there is no ALTER SCHEMA … RENAME. Create the new schema and transfer its objects instead.");

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

        // Every constraint (primary key, unique, check, foreign keys) is created inline; only indexes arrive as
        // separate CREATE INDEX actions from the linearizer.
        var parts = table.Columns.Select(BuildColumnDef)
            .Concat(InlineConstraintClauses(table))
            .ToList();

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

    protected override Result<IReadOnlyList<SqlStatement>> AlterColumn(AlterColumn action) =>
        Statement($"ALTER TABLE {Qualify(action.Table)} ALTER COLUMN {Quote(action.Column.Name)} {TypeSql(action.Column.Type)}{NullableSql(action.Column.IsNullable)}");

    protected override Result<IReadOnlyList<SqlStatement>> AlterIdentitySequence(AlterIdentitySequence action) =>
        RequiresRecreate($"SQL Server cannot change the seed or increment of identity column {action.Column} in place; this requires rebuilding the table. Recreate the column or table instead.");

    protected override Result<IReadOnlyList<SqlStatement>> SetColumnGenerated(SetColumnGenerated action) =>
        RequiresRecreate($"SQL Server cannot change the expression of computed column {action.Column} in place; this requires rebuilding the table. Recreate the column or table instead.");

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
        var sql = IndexSql(action.Table, action.Index, action.OnView);
        return sql.IsFailure
            ? Result.Failure<IReadOnlyList<SqlStatement>>(sql.Diagnostics)
            : Statement(sql.Require());
    }

    /// <summary>
    /// One <c>CREATE INDEX</c>, or the reason SQL Server has no way to write it.
    /// </summary>
    /// <param name="owner">The table or view the index attaches to.</param>
    /// <param name="idx">The index to render.</param>
    /// <param name="onView">
    /// Whether the owner is a view. An indexed view's index has to be the clustered one — SQL Server refuses a
    /// nonclustered index on a view that has no unique clustered index — and only one index on a relation can be
    /// clustered, so the unique index that makes the view an indexed view is it.
    /// </param>
    private Result<string> IndexSql(ObjectAddress owner, TableIndex idx, bool onView)
    {
        if (idx.Xml is { } xml)
        {
            return XmlIndexSql(owner, idx, xml);
        }

        if (idx.Method is not null)
        {
            return UnsupportedFragment($"SQL Server indexes have no access method (USING) — index {idx.Name} specifies {idx.Method}.");
        }

        var keys = new List<string>();
        foreach (var col in idx.Columns)
        {
            if (col.Column is null)
            {
                return UnsupportedFragment($"SQL Server cannot index the expression {col.Expression} directly; add a computed column and index that instead.");
            }

            if (col.Nulls != IndexNulls.Default)
            {
                return UnsupportedFragment($"SQL Server indexes do not support NULLS FIRST / NULLS LAST ordering (index {idx.Name}).");
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

        // What the index says, when it says anything. An indexed view is the exception: its unique index has
        // to be the clustered one, so an undeclared index on a view still clusters.
        var clustered = idx.Clustered switch
        {
            true => "CLUSTERED ",
            false => "NONCLUSTERED ",
            null when onView && idx.IsUnique => "CLUSTERED ",
            null => "",
        };
        var include = idx.Include.Count > 0 ? $" INCLUDE ({ColumnList(idx.Include)})" : "";
        var sql = $"CREATE {unique}{clustered}INDEX {Quote(idx.Name)} ON {Qualify(owner)} ({string.Join(", ", keys)}){include}";
        return Result.Success(idx.Predicate is { } predicate ? $"{sql} WHERE {predicate}" : sql);
    }

    /// <summary>
    /// One XML index: the node table itself, or a b-tree over one that already exists.
    /// </summary>
    /// <remarks>
    /// An XML index indexes a shredded document rather than a value, so the facets of an ordinary index have
    /// nothing to mean here — SQL Server accepts no uniqueness, no <c>INCLUDE</c>, and no filter on one, and its
    /// single key names the XML column. Each is refused rather than dropped silently.
    /// </remarks>
    private Result<string> XmlIndexSql(ObjectAddress owner, TableIndex idx, XmlIndexDefinition xml)
    {
        if (idx.IsUnique || idx.Include.Count > 0 || idx.Predicate is not null || idx.Method is not null)
        {
            return UnsupportedFragment($"XML index {idx.Name} cannot be unique, carry INCLUDE columns, take a filter, or name an access method.");
        }

        if (idx.Columns is not [{ Column: { } column, Sort: IndexSort.Default, Nulls: IndexNulls.Default }])
        {
            return UnsupportedFragment($"XML index {idx.Name} indexes exactly one XML column, without a sort direction.");
        }

        var target = $"{Quote(idx.Name)} ON {Qualify(owner)} ({Quote(column)})";
        if (xml.IsPrimary)
        {
            return Result.Success($"CREATE PRIMARY XML INDEX {target}");
        }

        if (xml.PrimaryIndex is not { } primary)
        {
            return UnsupportedFragment($"XML index {idx.Name} is a {xml.Kind} index, so it must name the primary XML index whose node table it reads.");
        }

        var kind = xml.Kind switch
        {
            XmlIndexKind.Path => "PATH",
            XmlIndexKind.Value => "VALUE",
            _ => "PROPERTY",
        };
        return Result.Success($"CREATE XML INDEX {target} USING XML INDEX {Quote(primary)} FOR {kind}");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropIndex(DropIndex action) =>
        Statement($"DROP INDEX {Quote(action.Index.Member)} ON {Qualify(action.Index.Owner)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetIndexComment(SetIndexComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Index.Schema), ("TABLE", action.Index.Object), ("INDEX", action.Index.Member));

    // ── Triggers ──────────────────────────────────────────────────────────────

    // CREATE [OR ALTER] TRIGGER [s].[name] ON [s].[table] {AFTER|INSTEAD OF} {events} AS <body> — a create is
    // a plain CREATE (a collision means the database drifted from the plan's belief); a replacement is
    // CREATE OR ALTER, which the plan emits only when it knows the trigger exists. SQL Server triggers are
    // statement-level, fire only AFTER or INSTEAD OF, carry no WHEN clause and run an inline body — facets of
    // the model that don't map (BEFORE, row-level, WHEN, TRUNCATE, UPDATE OF, a function indirection) are rejected.
    protected override Result<IReadOnlyList<SqlStatement>> CreateTrigger(CreateTrigger action) =>
        RenderTrigger(action.Table, action.Trigger, orAlter: false);

    protected override Result<IReadOnlyList<SqlStatement>> ReplaceTrigger(ReplaceTrigger action) =>
        RenderTrigger(action.Table, action.Trigger, orAlter: true);

    private Result<IReadOnlyList<SqlStatement>> RenderTrigger(ObjectAddress table, Trigger trigger, bool orAlter)
    {
        if (trigger.Body is not { } body)
        {
            return Unsupported($"SQL Server triggers run an inline body, but trigger {trigger.Name} has none (it calls a function). Declare it with an AS $$ … $$ body instead.");
        }

        if (trigger.Timing == TriggerTiming.Before)
        {
            return Unsupported($"SQL Server does not support BEFORE triggers (trigger {trigger.Name}); only AFTER and INSTEAD OF are available.");
        }

        if (trigger.Level == TriggerLevel.Row)
        {
            return Unsupported($"SQL Server does not support row-level (FOR EACH ROW) triggers (trigger {trigger.Name}); triggers fire once per statement — use the inserted/deleted tables.");
        }

        if (trigger.When is not null)
        {
            return Unsupported($"SQL Server does not support a trigger WHEN condition (trigger {trigger.Name}); put the guard inside the body, e.g. IF UPDATE(column).");
        }

        if (trigger.Events.HasFlag(TriggerEvent.Truncate))
        {
            return Unsupported($"SQL Server does not support TRUNCATE triggers (trigger {trigger.Name}).");
        }

        if (trigger.UpdateOfColumns.Count > 0)
        {
            return Unsupported($"SQL Server does not support UPDATE OF (columns) on a trigger (trigger {trigger.Name}); use IF UPDATE(column) inside the body.");
        }

        var timing = trigger.Timing == TriggerTiming.InsteadOf ? "INSTEAD OF" : "AFTER";
        var notForReplication = trigger.IsNotForReplication ? " NOT FOR REPLICATION" : "";
        return Statement($"CREATE {(orAlter ? "OR ALTER " : "")}TRIGGER {Qualify(table.Schema, trigger.Name)} ON {Qualify(table)} {timing} {TriggerEventsSql(trigger.Events)}{notForReplication} AS {body}");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropTrigger(DropTrigger action) =>
        Statement($"DROP TRIGGER {Qualify(action.Trigger.Schema, action.Trigger.Member)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetTriggerComment(SetTriggerComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Trigger.Schema), ("TABLE", action.Trigger.Object), ("TRIGGER", action.Trigger.Member));

    // ── XML schema collections ────────────────────────────────────────────────

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> CreateXmlSchemaCollection(CreateXmlSchemaCollection action) =>
        Statement($"CREATE XML SCHEMA COLLECTION {Qualify(action.SchemaName, action.Collection.Name)} AS {action.Collection.Body}");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> DropXmlSchemaCollection(DropXmlSchemaCollection action) =>
        Statement($"DROP XML SCHEMA COLLECTION {Qualify(action.Collection)}");

    // ── Views ─────────────────────────────────────────────────────────────────

    // A create is a plain CREATE: if the view already exists, the database has drifted from the plan's
    // belief, and SQL Server saying so is the correct outcome. A view's indexes ride its definition (the
    // linearizer emits no separate CreateIndex for a created view), so they render here.
    protected override Result<IReadOnlyList<SqlStatement>> CreateView(CreateView action)
    {
        if (action.View.IsMaterialized)
        {
            return Unsupported(action);
        }

        return ViewStatements(action.SchemaName, action.View,
            $"CREATE VIEW {Qualify(action.SchemaName, action.View.Name)}{SchemaBinding(action.View)} AS {action.View.Body}");
    }

    // A body or binding change replaces in place; the plan knows the view exists, so OR ALTER is honest here.
    // Unlike a create, this renders the view alone: the view survives the statement and so do its indexes, so
    // the core diffs them separately and any that are new arrive as their own CreateIndex.
    protected override Result<IReadOnlyList<SqlStatement>> ReplaceView(ReplaceView action) =>
        action.View.IsMaterialized
            ? Unsupported(action)
            : Statement($"CREATE OR ALTER VIEW {Qualify(action.SchemaName, action.View.Name)}{SchemaBinding(action.View)} AS {action.View.Body}");

    /// <summary>
    /// The view's own statement, followed by the indexes it carries.
    /// </summary>
    private Result<IReadOnlyList<SqlStatement>> ViewStatements(SqlIdentifier schemaName, View view, string definition)
    {
        var owner = new ObjectAddress(schemaName, view.Name, SchemaObjectKind.View);
        var statements = new List<SqlStatement> { new(definition) };

        foreach (var index in view.Indexes)
        {
            var sql = IndexSql(owner, index, onView: true);
            if (sql.IsFailure)
            {
                return Result.Failure<IReadOnlyList<SqlStatement>>(sql.Diagnostics);
            }
            statements.Add(new SqlStatement(sql.Require()));
        }

        return Statements([.. statements]);
    }

    /// <summary>
    /// The <c>WITH SCHEMABINDING</c> clause, declared on the view rather than inferred from its indexes.
    /// </summary>
    /// <remarks>
    /// SQL Server refuses to index a view that is not schema-bound, so an indexed view is always bound — but the
    /// converse does not hold, and a binding read off the indexes could not survive a view that is bound without
    /// being indexed, nor an index added to a view that already exists unbound.
    /// </remarks>
    private static string SchemaBinding(View view) => view.IsSchemaBound ? " WITH SCHEMABINDING" : "";

    protected override Result<IReadOnlyList<SqlStatement>> RenameView(RenameView action) =>
        action.IsMaterialized
            ? Unsupported(action)
            : Statement(RenameObject(action.View, action.NewName));

    protected override Result<IReadOnlyList<SqlStatement>> SetViewComment(SetViewComment action) =>
        action.IsMaterialized
            ? Unsupported(action)
            : ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.View.Schema), ("VIEW", action.View.Name));

    // ── Domains (alias types: CREATE TYPE … FROM) ─────────────────────────────
    // An alias type carries a base type and nullability, and nothing else: defaults and check constraints
    // have no SQL Server equivalent, and there is no ALTER TYPE — a change is a drop and recreate.

    protected override Result<IReadOnlyList<SqlStatement>> CreateDomain(CreateDomain action)
    {
        if (DomainGuard(action.DomainType) is { } blocked)
        {
            return blocked;
        }

        return Statement(CreateAliasType(action.SchemaName, action.DomainType));
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropDomain(DropDomain action) =>
        Statement($"DROP TYPE {Qualify(action.Domain)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameDomain(RenameDomain action) =>
        Statement($"EXEC sys.sp_rename @objname = N'{Lit(Qualify(action.Domain))}', @newname = N'{Lit(action.NewName.Value)}', @objtype = N'USERDATATYPE'");

    protected override Result<IReadOnlyList<SqlStatement>> RecreateDomain(RecreateDomain action)
    {
        if (DomainGuard(action.DomainType) is { } blocked)
        {
            return blocked;
        }

        // Valid only while nothing binds the type; SQL Server refuses to drop a type a column still uses,
        // which is the correct failure for a plan that did not rebuild the dependents first.
        return Statements(
            new SqlStatement($"DROP TYPE {Qualify(action.SchemaName, action.DomainType.Name)}"),
            new SqlStatement(CreateAliasType(action.SchemaName, action.DomainType)));
    }

    protected override Result<IReadOnlyList<SqlStatement>> AlterDomainNotNull(AlterDomainNotNull action) =>
        RequiresRecreate($"SQL Server cannot alter an alias type's nullability in place (type {action.Domain}); drop and recreate it.");

    protected override Result<IReadOnlyList<SqlStatement>> AlterDomainDefault(AlterDomainDefault action) =>
        Unsupported($"SQL Server alias types cannot carry a default (type {action.Domain}); declare the default on the columns that use the type.");

    protected override Result<IReadOnlyList<SqlStatement>> AddDomainCheck(AddDomainCheck action) =>
        Unsupported($"SQL Server alias types cannot carry check constraints (type {action.Domain}); declare the check on the tables that use the type.");

    protected override Result<IReadOnlyList<SqlStatement>> SetDomainComment(SetDomainComment action) =>
        ExtendedProperty(action.OldComment, action.NewComment, ("SCHEMA", action.Domain.Schema), ("TYPE", action.Domain.Name));

    private string CreateAliasType(SqlIdentifier schemaName, DomainType domain) =>
        $"CREATE TYPE {Qualify(schemaName, domain.Name)} FROM {TypeSql(domain.DataType)}{(domain.NotNull ? " NOT NULL" : "")}";

    private static Result<IReadOnlyList<SqlStatement>>? DomainGuard(DomainType domain) => domain switch
    {
        { Default: not null } => Unsupported($"SQL Server alias types cannot carry a default (domain {domain.Name}); declare the default on the columns that use the type."),
        { Checks.Count: > 0 } => Unsupported($"SQL Server alias types cannot carry check constraints (domain {domain.Name}); declare the checks on the tables that use the type."),
        _ => null,
    };

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
            return RequiresRecreate($"SQL Server cannot change the data type of sequence {action.Sequence} with ALTER SEQUENCE; drop and recreate the sequence instead.");
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

    // A create is a plain CREATE: if the routine already exists, the database has drifted from the plan's
    // belief, and SQL Server saying so is the correct outcome.
    protected override Result<IReadOnlyList<SqlStatement>> CreateRoutine(CreateRoutine action)
    {
        var routine = action.Routine;
        if (routine.RoutineKind == RoutineKind.Aggregate)
        {
            return AggregatesUnsupported(routine.Name);
        }

        return Statement($"CREATE {RoutineKeyword(routine.RoutineKind)} {Qualify(action.SchemaName, routine.Name)}{ParameterListSql(routine)} {routine.Definition}");
    }

    // Both replace an existing routine in place — a body change, and a signature change (one routine per
    // name, so a new signature replaces rather than overloading) — which the plan knows exists.
    protected override Result<IReadOnlyList<SqlStatement>> ReplaceRoutine(ReplaceRoutine action) =>
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
        routine.RoutineKind == RoutineKind.Aggregate
            ? AggregatesUnsupported(routine.Name)
            : Statement($"CREATE OR ALTER {RoutineKeyword(routine.RoutineKind)} {Qualify(schemaName, routine.Name)}{ParameterListSql(routine)} {routine.Definition}");

    // T-SQL rejects empty parentheses on a parameter-less procedure (a function keeps them), and a list
    // whose final line carries a comment would swallow the closing parenthesis printed after it, so such
    // a list gains a trailing newline.
    private static string ParameterListSql(Routine routine)
    {
        var text = routine.Arguments.Value;
        if (routine.RoutineKind == RoutineKind.Procedure && string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var guarded = SqlLexer.EndsInLineComment(text) ? $"{text}\n" : text;
        return $"({guarded})";
    }

    // SQL Server aggregates are CLR assemblies, not SQL declarations; the model's opaque definition cannot
    // express one, so they are a declared limitation rather than a wrong CREATE FUNCTION.
    private static Result<IReadOnlyList<SqlStatement>> AggregatesUnsupported(SqlIdentifier name) =>
        Unsupported($"SQL Server aggregates are CLR-backed; NSchema.SqlServer does not support them (aggregate {name}).");

    // ── Building blocks ───────────────────────────────────────────────────────

    private string BuildColumnDef(Column col)
    {
        // A computed (generated) column states no type — only its expression, persisted to storage.
        if (col.GeneratedExpression is { } generated)
        {
            // PERSISTED used to be unconditional, so every computed column came back written to storage whatever
            // the source said — a change to how the table is written and how much of it can be indexed.
            return $"{Quote(col.Name)} AS ({generated}){(col.IsStored ? " PERSISTED" : "")}";
        }

        var identity = col.IsIdentity ? BuildIdentityClause(col.IdentityOptions) : "";
        // Identity and DEFAULT are mutually exclusive on SQL Server; the core's structural policy keeps a default
        // off an identity column, so this only adds a default to a plain column.
        // A named default is worth emitting because the alternative is SQL Server inventing one
        // (DF__Departmen__Modif__37A5467C) that cannot be predicted and so cannot later be referred to.
        var defaultName = col.DefaultConstraintName is { } n ? $" CONSTRAINT {Quote(n)}" : "";
        var def = col is { DefaultExpression: { } d, IsIdentity: false } ? $"{defaultName} DEFAULT {d}" : "";
        // ROWGUIDCOL sits between the type and the nullability, which is where T-SQL writes it.
        var rowGuid = col.IsRowGuid ? " ROWGUIDCOL" : "";
        return $"{Quote(col.Name)} {TypeSql(col.Type)}{identity}{rowGuid}{NullableSql(col.IsNullable)}{def}";
    }

    // SQL Server identity uses a (seed, increment) pair; there is no minimum-value concept, so
    // IdentityOptions.MinValue is not expressible and is ignored. Absent options default to IDENTITY(1, 1).
    private static string BuildIdentityClause(IdentityOptions? options) =>
        $" IDENTITY({options?.StartWith ?? 1}, {options?.IncrementBy ?? 1})"
        + (options is { NotForReplication: true } ? " NOT FOR REPLICATION" : "");

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

    // A typed xml names the collection it validates against where another type carries a length or precision;
    // without it the column is untyped, and an XQuery expression over it no longer binds.
    private string TypeSql(SqlType type) => type.Xml is { } xml
        ? $"xml({(xml.IsDocument ? "DOCUMENT" : "CONTENT")} {Qualify(xml.Collection)})"
        : BareTypeSql(type);

    private string BareTypeSql(SqlType type) => type.Name.Value.ToLowerInvariant() switch
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
        "datetime" => "datetime",
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
