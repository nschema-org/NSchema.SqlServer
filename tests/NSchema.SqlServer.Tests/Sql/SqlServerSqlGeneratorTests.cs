using Microsoft.Data.SqlClient;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Sequence;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.SqlServer.Sql;
using NSchema.SqlServer.Tests.Fixtures;
using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Constraints;
using NSchema.Schema.Model.Indexes;
using NSchema.Schema.Model.Routines;
using NSchema.Schema.Model.Sequences;
using NSchema.Schema.Model.Tables;
using NSchema.Schema.Model.Triggers;
using NSchema.Schema.Model.Views;
using NSchema.Sql.Model;

namespace NSchema.SqlServer.Tests.Sql;

/// <summary>
/// Executes the generated T-SQL against a real SQL Server (via Testcontainers) to prove it is valid, and round-trips
/// the important surfaces back through <see cref="SqlServerSchemaProvider"/> to prove apply → introspect is stable.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerSqlGeneratorTests(SqlServerContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _connectionString = fixture.ConnectionString;
    private readonly string _schema = $"test_{Guid.NewGuid():N}";
    private readonly SqlServerSqlGenerator _generator = new();
    private SqlConnection _conn = null!;
    private SqlServerSchemaProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        _conn = new SqlConnection(_connectionString);
        await _conn.OpenAsync();
        _provider = new SqlServerSchemaProvider(new SqlServerConnectionSource(_connectionString));
        await Exec($"CREATE SCHEMA [{_schema}]");
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupSchema();
        await _conn.DisposeAsync();
    }

    private async Task Apply(params MigrationAction[] actions)
    {
        var plan = _generator.Generate(new MigrationPlan(actions, [], []));
        foreach (var statement in plan.Statements)
        {
            await Exec(statement.Sql);
        }
    }

    // ── Tables, columns ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTable_WithIdentityPrimaryKey_CreatesTableAndIdentity()
    {
        await Apply(new CreateTable(_schema, new Table("users",
            PrimaryKey: new PrimaryKey("pk_users", ["id"]),
            Columns: [new Column("id", SqlType.BigInt, IsNullable: false, IsIdentity: true)])));

        (await ScalarBool($"SELECT CAST(CASE WHEN OBJECT_ID(N'[{_schema}].[users]') IS NOT NULL THEN 1 ELSE 0 END AS bit)")).ShouldBeTrue();
        (await ScalarBool($"SELECT COLUMNPROPERTY(OBJECT_ID(N'[{_schema}].[users]'), 'id', 'IsIdentity')")).ShouldBeTrue();
    }

    [Fact]
    public async Task AddRenameDropColumn_AreApplied()
    {
        await Exec($"CREATE TABLE [{_schema}].[items] (id int)");

        await Apply(new AddColumn(_schema, "items", new Column("name", SqlType.VarChar(100), IsNullable: false)));
        (await ColumnType("items", "name")).ShouldBe("varchar");

        await Apply(new RenameColumn(_schema, "items", "name", "label"));
        (await ColumnExists("items", "label")).ShouldBeTrue();

        await Apply(new DropColumn(_schema, "items", new Column("label", SqlType.VarChar(100))));
        (await ColumnExists("items", "label")).ShouldBeFalse();
    }

    [Fact]
    public async Task AlterColumnType_PreservesNotNull_WhenOnlyTypeChanges()
    {
        // The SQL Server gotcha: ALTER COLUMN that omits nullability resets the column to NULL. The action carries the
        // column's (unchanged) nullability so the generated statement restates NOT NULL.
        await Exec($"CREATE TABLE [{_schema}].[t] (c int NOT NULL)");

        await Apply(new AlterColumnType(_schema, "t", "c", SqlType.Int, SqlType.BigInt, IsNullable: false));

        (await ColumnType("t", "c")).ShouldBe("bigint");
        (await ColumnIsNullable("t", "c")).ShouldBeFalse();
    }

    [Fact]
    public async Task AlterColumnNullability_RestatesType_WhenOnlyNullabilityChanges()
    {
        await Exec($"CREATE TABLE [{_schema}].[t] (c varchar(50) NOT NULL)");

        await Apply(new AlterColumnNullability(_schema, "t", "c", OldNullable: false, NewNullable: true, ColumnType: SqlType.VarChar(50)));

        (await ColumnType("t", "c")).ShouldBe("varchar");
        (await ColumnIsNullable("t", "c")).ShouldBeTrue();
    }

    [Fact]
    public async Task AlterColumn_TypeAndNullabilityTogether_FoldIntoOneStatement()
    {
        await Exec($"CREATE TABLE [{_schema}].[t] (c int NULL)");

        // Both actions for one column; the generator emits a single ALTER COLUMN. (Two ALTER COLUMNs would also work,
        // but folding is what the generator does — and the column must end as bigint NOT NULL.)
        var plan = _generator.Generate(new MigrationPlan(
        [
            new AlterColumnType(_schema, "t", "c", SqlType.Int, SqlType.BigInt, IsNullable: false),
            new AlterColumnNullability(_schema, "t", "c", OldNullable: true, NewNullable: false, ColumnType: SqlType.BigInt),
        ], [], []));

        plan.Statements.Count(s => s.Sql.Contains("ALTER COLUMN")).ShouldBe(1);
        foreach (var statement in plan.Statements)
        {
            await Exec(statement.Sql);
        }

        (await ColumnType("t", "c")).ShouldBe("bigint");
        (await ColumnIsNullable("t", "c")).ShouldBeFalse();
    }

    [Fact]
    public async Task SetColumnDefault_AddsThenDrops()
    {
        await Exec($"CREATE TABLE [{_schema}].[t] (id int, quantity int)");

        await Apply(new SetColumnDefault(_schema, "t", "quantity", null, "0"));
        (await ScalarBool($"SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID(N'[{_schema}].[t]') AND c.name='quantity') THEN 1 ELSE 0 END AS bit)")).ShouldBeTrue();

        await Apply(new SetColumnDefault(_schema, "t", "quantity", "0", null));
        (await ScalarBool($"SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID(N'[{_schema}].[t]') AND c.name='quantity') THEN 1 ELSE 0 END AS bit)")).ShouldBeFalse();
    }

    [Fact]
    public async Task RoundTrip_ComputedColumn_IntrospectsAsGenerated()
    {
        await Apply(new CreateTable(_schema, new Table("boxes", Columns:
        [
            new Column("w", SqlType.Int, IsNullable: false),
            new Column("h", SqlType.Int, IsNullable: false),
            new Column("area", SqlType.Int, GeneratedExpression: "w * h"),
        ])));

        var area = (await Introspect()).Tables.ShouldHaveSingleItem().Columns.Single(c => c.Name == "area");
        area.GeneratedExpression.ShouldNotBeNull();
        area.GeneratedExpression!.ShouldContain("w");
        area.DefaultExpression.ShouldBeNull();
    }

    // ── Constraints, indexes ──────────────────────────────────────────────────

    [Fact]
    public async Task Constraints_AddAndDrop()
    {
        await Exec($"CREATE TABLE [{_schema}].[parent] (id int NOT NULL, CONSTRAINT pk_parent PRIMARY KEY (id))");
        await Exec($"CREATE TABLE [{_schema}].[child] (id int NOT NULL, parent_id int, code varchar(20), balance int)");

        await Apply(
            new AddForeignKey(_schema, "child", new ForeignKey("fk_child_parent", ["parent_id"], _schema, "parent", ["id"], OnDelete: ReferentialAction.Cascade)),
            new AddUniqueConstraint(_schema, "child", new UniqueConstraint("uq_child_code", ["code"])),
            new AddCheckConstraint(_schema, "child", new CheckConstraint("ck_child_balance", "balance >= 0")));

        var table = (await Introspect()).Tables.Single(t => t.Name == "child");
        table.ForeignKeys.ShouldHaveSingleItem().Name.ShouldBe("fk_child_parent");
        table.UniqueConstraints.ShouldHaveSingleItem().Name.ShouldBe("uq_child_code");
        table.CheckConstraints.ShouldHaveSingleItem().Name.ShouldBe("ck_child_balance");

        await Apply(
            new DropForeignKey(_schema, "child", "fk_child_parent"),
            new DropUniqueConstraint(_schema, "child", "uq_child_code"),
            new DropCheckConstraint(_schema, "child", "ck_child_balance"));

        var dropped = (await Introspect()).Tables.Single(t => t.Name == "child");
        dropped.ForeignKeys.ShouldBeEmpty();
        dropped.UniqueConstraints.ShouldBeEmpty();
        dropped.CheckConstraints.ShouldBeEmpty();
    }

    [Fact]
    public async Task RoundTrip_RichIndex_PreservesIncludeFilterAndDescending()
    {
        await Exec($"CREATE TABLE [{_schema}].[items] (id int, name varchar(50), qty int)");

        await Apply(new CreateIndex(_schema, "items", new TableIndex("idx_items_rich",
            [new IndexColumn("id", Sort: IndexSort.Descending)], IsUnique: true, Predicate: "qty > 0", Include: ["name"])));

        var index = (await Introspect()).Tables.Single(t => t.Name == "items").Indexes.ShouldHaveSingleItem();
        index.Name.ShouldBe("idx_items_rich");
        index.IsUnique.ShouldBeTrue();
        index.Include.ShouldBe(["name"]);
        index.Columns.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Expression.ShouldBe("id"),
            c => c.Sort.ShouldBe(IndexSort.Descending));
        index.Predicate.ShouldNotBeNull();
        index.Predicate!.ShouldContain("qty");
    }

    // ── Views, sequences, routines ────────────────────────────────────────────

    [Fact]
    public async Task View_CreateReplaceRenameDrop_RoundTripsBody()
    {
        await Exec($"CREATE TABLE [{_schema}].[users] (id int, email varchar(50))");

        await Apply(new CreateView(_schema, new View("u", $"SELECT id FROM [{_schema}].[users]")));
        // CREATE OR ALTER replaces in place.
        await Apply(new CreateView(_schema, new View("u", $"SELECT id, email FROM [{_schema}].[users]")));

        var view = (await Introspect()).Views.ShouldHaveSingleItem();
        view.Name.ShouldBe("u");
        view.Body.ShouldContain("email");

        await Apply(new RenameView(_schema, "u", "u2"));
        (await Introspect()).Views.ShouldHaveSingleItem().Name.ShouldBe("u2");

        await Apply(new DropView(_schema, "u2"));
        (await Introspect()).Views.ShouldBeEmpty();
    }

    [Fact]
    public async Task RoundTrip_FullyOptionedSequence_IntrospectsToSameOptions()
    {
        var options = new SequenceOptions(SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true);

        await Apply(new CreateSequence(_schema, new Sequence("order_id", options)));

        (await Introspect()).Sequences.ShouldHaveSingleItem().Options.ShouldBe(options);
    }

    [Fact]
    public async Task RoundTrip_BareSequence_IntrospectsToAllNullOptions()
    {
        await Apply(new CreateSequence(_schema, new Sequence("order_id")));

        (await Introspect()).Sequences.ShouldHaveSingleItem().Options.ShouldBe(new SequenceOptions());
    }

    [Fact]
    public async Task Function_CreateRecreateDrop()
    {
        await Apply(new CreateRoutine(_schema, new Routine("add_one", RoutineKind.Function, "@a int",
            "RETURNS int AS BEGIN RETURN @a + 1 END")));
        (await ScalarInt($"SELECT [{_schema}].[add_one](41)")).ShouldBe(42);

        // A signature change is replaced in place by CREATE OR ALTER (no overload, no lost comment).
        await Apply(new RecreateRoutine(_schema, new Routine("add_one", RoutineKind.Function, "@a int, @b int",
            "RETURNS int AS BEGIN RETURN @a + @b END")));
        (await ScalarInt($"SELECT [{_schema}].[add_one](40, 2)")).ShouldBe(42);

        await Apply(new DropRoutine(_schema, "add_one", RoutineKind.Function));
        (await ScalarBool($"SELECT CAST(CASE WHEN OBJECT_ID(N'[{_schema}].[add_one]') IS NOT NULL THEN 1 ELSE 0 END AS bit)")).ShouldBeFalse();
    }

    [Fact]
    public async Task Procedure_CreateAndDrop()
    {
        await Exec($"CREATE TABLE [{_schema}].[log] (msg varchar(100))");

        await Apply(new CreateRoutine(_schema, new Routine("write_log", RoutineKind.Procedure, "@msg varchar(100)",
            $"AS BEGIN INSERT INTO [{_schema}].[log] (msg) VALUES (@msg) END")));
        await Exec($"EXEC [{_schema}].[write_log] @msg = 'hi'");
        (await ScalarString($"SELECT msg FROM [{_schema}].[log]")).ShouldBe("hi");

        await Apply(new DropRoutine(_schema, "write_log", RoutineKind.Procedure));
        (await ScalarBool($"SELECT CAST(CASE WHEN OBJECT_ID(N'[{_schema}].[write_log]') IS NOT NULL THEN 1 ELSE 0 END AS bit)")).ShouldBeFalse();
    }

    // ── Triggers ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Trigger_IntrospectsBodyEventsTimingAndComment()
    {
        await Exec($"CREATE TABLE [{_schema}].[users] (id int)");
        await Exec($"CREATE TABLE [{_schema}].[audit] (msg varchar(50), n int)");
        // A genuinely multi-statement body (its own internal ';') exercised end to end: generate → execute → introspect.
        var trigger = new Trigger("users_audit", TriggerTiming.After, TriggerEvent.Insert | TriggerEvent.Update,
            Body: $"""
                BEGIN
                    DECLARE @c int = (SELECT COUNT(*) FROM inserted);
                    INSERT INTO [{_schema}].[audit] (msg, n) VALUES ('changed', @c);
                END
                """);

        await Apply(new CreateTrigger(_schema, "users", trigger));

        var introspected = (await Introspect()).Tables.Single(t => t.Name == "users").Triggers.ShouldHaveSingleItem();
        introspected.Name.ShouldBe("users_audit");
        introspected.Timing.ShouldBe(TriggerTiming.After);
        introspected.Events.ShouldBe(TriggerEvent.Insert | TriggerEvent.Update);
        introspected.Level.ShouldBe(TriggerLevel.Statement);
        introspected.Function.ShouldBeNull();
        introspected.Body.ShouldNotBeNull();
        introspected.Body!.ShouldContain("DECLARE");
        introspected.Body.ShouldContain("INSERT INTO");

        // CREATE OR ALTER replaces in place; the trigger fires and the body's multiple statements run (an audit row
        // recording the inserted count lands).
        await Exec($"INSERT INTO [{_schema}].[users] (id) VALUES (1)");
        (await ScalarInt($"SELECT n FROM [{_schema}].[audit]")).ShouldBe(1);

        // Comment, then drop.
        await Apply(new SetTriggerComment(_schema, "users", "users_audit", null, "audit changes"));
        (await Introspect()).Tables.Single(t => t.Name == "users").Triggers.ShouldHaveSingleItem().Comment.ShouldBe("audit changes");

        await Apply(new DropTrigger(_schema, "users", "users_audit"));
        (await Introspect()).Tables.Single(t => t.Name == "users").Triggers.ShouldBeEmpty();
    }

    [Fact]
    public async Task RoundTrip_InsteadOfTrigger_IntrospectsTiming()
    {
        await Exec($"CREATE TABLE [{_schema}].[t] (id int)");
        var trigger = new Trigger("t_guard", TriggerTiming.InsteadOf, TriggerEvent.Delete,
            Body: "BEGIN RETURN; END");

        await Apply(new CreateTrigger(_schema, "t", trigger));

        var introspected = (await Introspect()).Tables.Single(t => t.Name == "t").Triggers.ShouldHaveSingleItem();
        introspected.Timing.ShouldBe(TriggerTiming.InsteadOf);
        introspected.Events.ShouldBe(TriggerEvent.Delete);
    }

    [Fact]
    public void BeforeTrigger_IsRejected()
    {
        var plan = new MigrationPlan([new CreateTrigger(_schema, "users",
            new Trigger("t", TriggerTiming.Before, TriggerEvent.Insert, Body: "BEGIN END"))], [], []);

        Should.Throw<NotSupportedException>(() => _generator.Generate(plan));
    }

    // ── Comments, grants ──────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Comments_OnTableAndColumn()
    {
        await Exec($"CREATE TABLE [{_schema}].[users] (id int, email varchar(50))");

        await Apply(
            new SetTableComment(_schema, "users", null, "Registered users"),
            new SetColumnComment(_schema, "users", "email", null, "Login address"));

        var table = (await Introspect()).Tables.ShouldHaveSingleItem();
        table.Comment.ShouldBe("Registered users");
        table.Columns.Single(c => c.Name == "email").Comment.ShouldBe("Login address");

        // Update then clear the table comment.
        await Apply(new SetTableComment(_schema, "users", "Registered users", "All users"));
        (await Introspect()).Tables.ShouldHaveSingleItem().Comment.ShouldBe("All users");

        await Apply(new SetTableComment(_schema, "users", "All users", null));
        (await Introspect()).Tables.ShouldHaveSingleItem().Comment.ShouldBeNull();
    }

    [Fact]
    public async Task RoundTrip_TableGrant()
    {
        await Exec($"CREATE TABLE [{_schema}].[users] (id int)");
        await Exec($"CREATE ROLE [{_schema}_role]");

        await Apply(new GrantTablePrivileges(_schema, "users", $"{_schema}_role", TablePrivilege.Select | TablePrivilege.Insert));

        // Deterministic: the generated GRANT lands in the catalog (SELECT + INSERT to the role).
        var granted = await ScalarInt($"""
            SELECT COUNT(*) FROM sys.database_permissions dp
            JOIN sys.database_principals pr ON pr.principal_id = dp.grantee_principal_id
            WHERE dp.major_id = OBJECT_ID(N'[{_schema}].[users]') AND dp.minor_id = 0 AND pr.name = '{_schema}_role'
              AND dp.state IN ('G', 'W') AND dp.permission_name IN ('SELECT', 'INSERT')
            """);
        granted.ShouldBe(2);

        // The provider surfaces the grant. SQL Server can briefly hide a freshly-permissioned object from a new
        // connection's catalog read, so this tolerates a short metadata-propagation lag (not an issue in normal use,
        // where introspection precedes apply on a settled database).
        var grant = await Eventually(async () =>
            (await Introspect()).Tables.SingleOrDefault(t => t.Name == "users")?.Grants.SingleOrDefault(g => g.Role == $"{_schema}_role"));
        grant.ShouldNotBeNull();
        grant.Privileges.HasFlag(TablePrivilege.Select).ShouldBeTrue();
        grant.Privileges.HasFlag(TablePrivilege.Insert).ShouldBeTrue();

        await Exec($"DROP ROLE [{_schema}_role]");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<NSchema.Schema.Model.Schemas.SchemaDefinition> Introspect()
    {
        var schema = await _provider.GetSchema([_schema], TestContext.Current.CancellationToken);
        return schema.Schemas.Single(s => s.Name == _schema);
    }

    // Retries a read until it returns a non-null result, tolerating SQL Server's brief metadata-propagation lag after
    // permission DDL. Returns null if it never materialises within the attempts.
    private static async Task<T?> Eventually<T>(Func<Task<T?>> read) where T : class
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await read() is { } result)
            {
                return result;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return null;
    }

    private async Task Exec(string sql)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<bool> ScalarBool(string sql) => Convert.ToBoolean(await Scalar(sql));

    private async Task<int> ScalarInt(string sql) => (int)(await Scalar(sql))!;

    private async Task<string?> ScalarString(string sql) => (await Scalar(sql)) as string;

    private async Task<object?> Scalar(string sql)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private Task<string?> ColumnType(string table, string column) => ScalarString($"""
        SELECT typ.name FROM sys.columns c JOIN sys.types typ ON typ.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'[{_schema}].[{table}]') AND c.name = '{column}'
        """);

    private Task<bool> ColumnIsNullable(string table, string column) => ScalarBool($"""
        SELECT c.is_nullable FROM sys.columns c
        WHERE c.object_id = OBJECT_ID(N'[{_schema}].[{table}]') AND c.name = '{column}'
        """);

    private Task<bool> ColumnExists(string table, string column) => ScalarBool($"""
        SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(N'[{_schema}].[{table}]') AND c.name = '{column}') THEN 1 ELSE 0 END AS bit)
        """);

    // Best-effort teardown: drop the schema's objects (foreign keys, then views, tables, sequences, routines) and the
    // schema itself. Failures are ignored — the container is discarded after the run and each test uses a unique schema.
    private async Task CleanupSchema()
    {
        try
        {
            await Exec($"""
                DECLARE @sql nvarchar(max) = N'';
                SELECT @sql += 'ALTER TABLE ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name) + ' DROP CONSTRAINT ' + QUOTENAME(fk.name) + ';'
                FROM sys.foreign_keys fk JOIN sys.tables t ON t.object_id = fk.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = '{_schema}';
                SELECT @sql += 'DROP VIEW ' + QUOTENAME(s.name) + '.' + QUOTENAME(o.name) + ';' FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = '{_schema}' AND o.type = 'V';
                SELECT @sql += 'DROP ' + CASE WHEN o.type = 'P' THEN 'PROCEDURE' WHEN o.type IN ('FN','IF','TF') THEN 'FUNCTION' ELSE 'TABLE' END + ' ' + QUOTENAME(s.name) + '.' + QUOTENAME(o.name) + ';'
                FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = '{_schema}' AND o.type IN ('U','P','FN','IF','TF');
                SELECT @sql += 'DROP SEQUENCE ' + QUOTENAME(s.name) + '.' + QUOTENAME(o.name) + ';' FROM sys.sequences o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = '{_schema}';
                EXEC sp_executesql @sql;
                DROP SCHEMA [{_schema}];
                """);
        }
        catch
        {
            // Ignore — isolated, ephemeral schema.
        }
    }
}
