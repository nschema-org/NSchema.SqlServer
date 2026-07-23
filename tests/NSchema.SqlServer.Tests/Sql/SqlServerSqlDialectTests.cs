using Microsoft.Data.SqlClient;
using NSchema.Diff.Model;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Constraints;
using NSchema.Model.Indexes;
using NSchema.Model.Routines;
using NSchema.Model.Sequences;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Model.Views;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Sequences;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.SqlServer.Sql;
using NSchema.SqlServer.Tests.Fixtures;

namespace NSchema.SqlServer.Tests.Sql;

/// <summary>
/// Executes the rendered T-SQL against a real SQL Server (via Testcontainers) to prove it is valid, and round-trips
/// the important surfaces back through <see cref="SqlServerDatabaseIntrospector"/> to prove apply → introspect is
/// stable.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerSqlDialectTests(SqlServerContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _connectionString = fixture.ConnectionString;
    private readonly string _schema = $"test_{Guid.NewGuid():N}";
    private readonly SqlServerSqlDialect _sut = new();
    private SqlConnection _conn = null!;
    private SqlServerDatabaseIntrospector _introspector = null!;

    public async ValueTask InitializeAsync()
    {
        _conn = new SqlConnection(_connectionString);
        await _conn.OpenAsync();
        _introspector = new SqlServerDatabaseIntrospector(new SqlServerConnectionSource(_connectionString));
        await Exec($"CREATE SCHEMA [{_schema}]");
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupSchema();
        await _conn.DisposeAsync();
    }

    private ObjectAddress Obj(string name) => new(_schema, name);

    private MemberAddress Mem(string owner, string member) => new(_schema, owner, member);

    private async Task Apply(params MigrationAction[] actions)
    {
        foreach (var statement in actions.SelectMany(action => _sut.Generate(action).Require()))
        {
            await Exec(statement.Sql.Value);
        }
    }

    // ── Tables, columns ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTable_WithIdentityPrimaryKey_CreatesTableAndIdentity()
    {
        await Apply(new CreateTable(_schema, new Table
        {
            Name = "users",
            PrimaryKey = new PrimaryKey { Name = "pk_users", ColumnNames = [new("id")] },
            Columns = [new Column { Name = "id", Type = SqlType.BigInt, IsIdentity = true }],
        }));

        (await ScalarBool($"SELECT CAST(CASE WHEN OBJECT_ID(N'[{_schema}].[users]') IS NOT NULL THEN 1 ELSE 0 END AS bit)")).ShouldBeTrue();
        (await ScalarBool($"SELECT COLUMNPROPERTY(OBJECT_ID(N'[{_schema}].[users]'), 'id', 'IsIdentity')")).ShouldBeTrue();
    }

    [Fact]
    public async Task AddRenameDropColumn_AreApplied()
    {
        await Exec($"CREATE TABLE [{_schema}].[items] (id int)");

        await Apply(new AddColumn(Obj("items"), new Column { Name = "name", Type = SqlType.VarChar(100) }));
        (await ColumnType("items", "name")).ShouldBe("varchar");

        await Apply(new RenameColumn(Mem("items", "name"), "label"));
        (await ColumnExists("items", "label")).ShouldBeTrue();

        await Apply(new DropColumn(Obj("items"), new Column { Name = "label", Type = SqlType.VarChar(100) }));
        (await ColumnExists("items", "label")).ShouldBeFalse();
    }

    [Fact]
    public async Task AlterColumn_PreservesNotNull_WhenOnlyTypeChanges()
    {
        // The SQL Server gotcha: ALTER COLUMN that omits nullability resets the column to NULL.
        await Exec($"CREATE TABLE [{_schema}].[t] (c int NOT NULL)");

        await Apply(new AlterColumn(Obj("t"), new Column { Name = "c", Type = SqlType.BigInt }, Type: new(SqlType.Int, SqlType.BigInt)));

        (await ColumnType("t", "c")).ShouldBe("bigint");
        (await ColumnIsNullable("t", "c")).ShouldBeFalse();
    }

    [Fact]
    public async Task AlterColumn_RestatesType_WhenOnlyNullabilityChanges()
    {
        await Exec($"CREATE TABLE [{_schema}].[t] (c varchar(50) NOT NULL)");

        await Apply(new AlterColumn(Obj("t"), new Column { Name = "c", Type = SqlType.VarChar(50), IsNullable = true }, Nullability: new(false, true)));

        (await ColumnType("t", "c")).ShouldBe("varchar");
        (await ColumnIsNullable("t", "c")).ShouldBeTrue();
    }

    [Fact]
    public async Task AlterColumn_TypeAndNullabilityTogether_EndsWithTheFinalColumn()
    {
        await Exec($"CREATE TABLE [{_schema}].[t] (c int NULL)");

        await Apply(new AlterColumn(Obj("t"), new Column { Name = "c", Type = SqlType.BigInt }, Type: new(SqlType.Int, SqlType.BigInt), Nullability: new(true, false)));

        (await ColumnType("t", "c")).ShouldBe("bigint");
        (await ColumnIsNullable("t", "c")).ShouldBeFalse();
    }

    [Fact]
    public async Task SetColumnDefault_AddsThenDrops()
    {
        await Exec($"CREATE TABLE [{_schema}].[t] (id int, quantity int)");

        await Apply(new SetColumnDefault(Mem("t", "quantity"), null, "0"));
        (await ScalarBool($"SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID(N'[{_schema}].[t]') AND c.name='quantity') THEN 1 ELSE 0 END AS bit)")).ShouldBeTrue();

        await Apply(new SetColumnDefault(Mem("t", "quantity"), "0", null));
        (await ScalarBool($"SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID(N'[{_schema}].[t]') AND c.name='quantity') THEN 1 ELSE 0 END AS bit)")).ShouldBeFalse();
    }

    [Fact]
    public async Task RoundTrip_ComputedColumn_IntrospectsAsGenerated()
    {
        await Apply(new CreateTable(_schema, new Table
        {
            Name = "boxes",
            Columns =
            [
                new Column { Name = "w", Type = SqlType.Int },
                new Column { Name = "h", Type = SqlType.Int },
                new Column { Name = "area", Type = SqlType.Int, GeneratedExpression = "w * h" },
            ],
        }));

        var area = (await Introspect()).Tables.ShouldHaveSingleItem().Columns.Single(c => c.Name == "area");
        area.GeneratedExpression.ShouldNotBeNull();
        area.GeneratedExpression.Value.ShouldContain("w");
        area.DefaultExpression.ShouldBeNull();
    }

    // ── Constraints, indexes ──────────────────────────────────────────────────

    [Fact]
    public async Task Constraints_AddAndDrop()
    {
        await Exec($"CREATE TABLE [{_schema}].[parent] (id int NOT NULL, CONSTRAINT pk_parent PRIMARY KEY (id))");
        await Exec($"CREATE TABLE [{_schema}].[child] (id int NOT NULL, parent_id int, code varchar(20), balance int)");

        await Apply(
            new AddForeignKey(Obj("child"), new ForeignKey
            {
                Name = "fk_child_parent",
                ColumnNames = [new("parent_id")],
                References = Obj("parent"),
                ReferencedColumnNames = [new("id")],
                OnDelete = ReferentialAction.Cascade,
            }),
            new AddUniqueConstraint(Obj("child"), new UniqueConstraint { Name = "uq_child_code", ColumnNames = [new("code")] }),
            new AddCheckConstraint(Obj("child"), new CheckConstraint { Name = "ck_child_balance", Expression = "balance >= 0" }));

        var table = (await Introspect()).Tables.Single(t => t.Name == "child");
        table.ForeignKeys.ShouldHaveSingleItem().Name.ShouldBe("fk_child_parent");
        table.UniqueConstraints.ShouldHaveSingleItem().Name.ShouldBe("uq_child_code");
        table.CheckConstraints.ShouldHaveSingleItem().Name.ShouldBe("ck_child_balance");

        await Apply(
            new DropForeignKey(Mem("child", "fk_child_parent")),
            new DropUniqueConstraint(Mem("child", "uq_child_code")),
            new DropCheckConstraint(Mem("child", "ck_child_balance")));

        var dropped = (await Introspect()).Tables.Single(t => t.Name == "child");
        dropped.ForeignKeys.ShouldBeEmpty();
        dropped.UniqueConstraints.ShouldBeEmpty();
        dropped.CheckConstraints.ShouldBeEmpty();
    }

    [Fact]
    public async Task RoundTrip_RichIndex_PreservesIncludeFilterAndDescending()
    {
        await Exec($"CREATE TABLE [{_schema}].[items] (id int, name varchar(50), qty int)");

        await Apply(new CreateIndex(Obj("items"), new TableIndex
        {
            Name = "idx_items_rich",
            Columns = [new IndexColumn(new SqlIdentifier("id"), Sort: IndexSort.Descending)],
            IsUnique = true,
            Predicate = "qty > 0",
            Include = [new("name")],
        }));

        var index = (await Introspect()).Tables.Single(t => t.Name == "items").Indexes.ShouldHaveSingleItem();
        index.Name.ShouldBe("idx_items_rich");
        index.IsUnique.ShouldBeTrue();
        index.Include.ShouldBe([new SqlIdentifier("name")]);
        index.Columns.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Column.ShouldBe(new SqlIdentifier("id")),
            c => c.Sort.ShouldBe(IndexSort.Descending));
        index.Predicate.ShouldNotBeNull();
        index.Predicate.Value.ShouldContain("qty");
    }

    // ── Views, sequences, routines ────────────────────────────────────────────

    [Fact]
    public async Task View_CreateReplaceRenameDrop_RoundTripsBody()
    {
        await Exec($"CREATE TABLE [{_schema}].[users] (id int, email varchar(50))");

        await Apply(new CreateView(_schema, new View { Name = "u", Body = $"SELECT id FROM [{_schema}].[users]" }));
        // CREATE OR ALTER replaces in place.
        await Apply(new CreateView(_schema, new View { Name = "u", Body = $"SELECT id, email FROM [{_schema}].[users]" }));

        var view = (await Introspect()).Views.ShouldHaveSingleItem();
        view.Name.ShouldBe("u");
        view.Body.Value.ShouldContain("email");

        await Apply(new RenameView(Obj("u"), "u2"));
        (await Introspect()).Views.ShouldHaveSingleItem().Name.ShouldBe("u2");

        await Apply(new DropView(Obj("u2")));
        (await Introspect()).Views.ShouldBeEmpty();
    }

    [Fact]
    public async Task RoundTrip_FullyOptionedSequence_IntrospectsToSameOptions()
    {
        var options = new SequenceOptions(SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true);

        await Apply(new CreateSequence(_schema, new Sequence { Name = "order_id", Options = options }));

        (await Introspect()).Sequences.ShouldHaveSingleItem().Options.ShouldBe(options);
    }

    [Fact]
    public async Task RoundTrip_BareSequence_IntrospectsToAllNullOptions()
    {
        await Apply(new CreateSequence(_schema, new Sequence { Name = "order_id" }));

        (await Introspect()).Sequences.ShouldHaveSingleItem().Options.ShouldBe(new SequenceOptions());
    }

    [Fact]
    public async Task Function_CreateRecreateDrop()
    {
        await Apply(new CreateRoutine(_schema, new Routine
        {
            Name = "add_one",
            RoutineKind = RoutineKind.Function,
            Arguments = "@a int",
            Definition = "RETURNS int AS BEGIN RETURN @a + 1 END",
        }));
        (await ScalarInt($"SELECT [{_schema}].[add_one](41)")).ShouldBe(42);

        // A signature change is replaced in place by CREATE OR ALTER (no overload, no lost comment).
        await Apply(new RecreateRoutine(_schema, new Routine
        {
            Name = "add_one",
            RoutineKind = RoutineKind.Function,
            Arguments = "@a int, @b int",
            Definition = "RETURNS int AS BEGIN RETURN @a + @b END",
        }));
        (await ScalarInt($"SELECT [{_schema}].[add_one](40, 2)")).ShouldBe(42);

        await Apply(new DropRoutine(Obj("add_one"), RoutineKind.Function));
        (await ScalarBool($"SELECT CAST(CASE WHEN OBJECT_ID(N'[{_schema}].[add_one]') IS NOT NULL THEN 1 ELSE 0 END AS bit)")).ShouldBeFalse();
    }

    [Fact]
    public async Task Procedure_CreateAndDrop()
    {
        await Exec($"CREATE TABLE [{_schema}].[log] (msg varchar(100))");

        await Apply(new CreateRoutine(_schema, new Routine
        {
            Name = "write_log",
            RoutineKind = RoutineKind.Procedure,
            Arguments = "@msg varchar(100)",
            Definition = $"AS BEGIN INSERT INTO [{_schema}].[log] (msg) VALUES (@msg) END",
        }));
        await Exec($"EXEC [{_schema}].[write_log] @msg = 'hi'");
        (await ScalarString($"SELECT msg FROM [{_schema}].[log]")).ShouldBe("hi");

        await Apply(new DropRoutine(Obj("write_log"), RoutineKind.Procedure));
        (await ScalarBool($"SELECT CAST(CASE WHEN OBJECT_ID(N'[{_schema}].[write_log]') IS NOT NULL THEN 1 ELSE 0 END AS bit)")).ShouldBeFalse();
    }

    // ── Triggers ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Trigger_IntrospectsBodyEventsTimingAndComment()
    {
        await Exec($"CREATE TABLE [{_schema}].[users] (id int)");
        await Exec($"CREATE TABLE [{_schema}].[audit] (msg varchar(50), n int)");
        // A genuinely multi-statement body (its own internal ';') exercised end to end: render → execute → introspect.
        var trigger = new Trigger
        {
            Name = "users_audit",
            Timing = TriggerTiming.After,
            Events = TriggerEvent.Insert | TriggerEvent.Update,
            Body = $"""
                BEGIN
                    DECLARE @c int = (SELECT COUNT(*) FROM inserted);
                    INSERT INTO [{_schema}].[audit] (msg, n) VALUES ('changed', @c);
                END
                """,
        };

        await Apply(new CreateTrigger(Obj("users"), trigger));

        var introspected = (await Introspect()).Tables.Single(t => t.Name == "users").Triggers.ShouldHaveSingleItem();
        introspected.Name.ShouldBe("users_audit");
        introspected.Timing.ShouldBe(TriggerTiming.After);
        introspected.Events.ShouldBe(TriggerEvent.Insert | TriggerEvent.Update);
        introspected.Level.ShouldBe(TriggerLevel.Statement);
        introspected.Function.ShouldBeNull();
        introspected.Body.ShouldNotBeNull();
        introspected.Body.Value.ShouldContain("DECLARE");
        introspected.Body.Value.ShouldContain("INSERT INTO");

        // CREATE OR ALTER replaces in place; the trigger fires and the body's multiple statements run (an audit row
        // recording the inserted count lands).
        await Exec($"INSERT INTO [{_schema}].[users] (id) VALUES (1)");
        (await ScalarInt($"SELECT n FROM [{_schema}].[audit]")).ShouldBe(1);

        // Comment, then drop.
        await Apply(new SetTriggerComment(Mem("users", "users_audit"), null, "audit changes"));
        (await Introspect()).Tables.Single(t => t.Name == "users").Triggers.ShouldHaveSingleItem().Comment.ShouldBe("audit changes");

        await Apply(new DropTrigger(Mem("users", "users_audit")));
        (await Introspect()).Tables.Single(t => t.Name == "users").Triggers.ShouldBeEmpty();
    }

    [Fact]
    public async Task RoundTrip_InsteadOfTrigger_IntrospectsTiming()
    {
        await Exec($"CREATE TABLE [{_schema}].[t] (id int)");
        var trigger = new Trigger
        {
            Name = "t_guard",
            Timing = TriggerTiming.InsteadOf,
            Events = TriggerEvent.Delete,
            Body = "BEGIN RETURN; END",
        };

        await Apply(new CreateTrigger(Obj("t"), trigger));

        var introspected = (await Introspect()).Tables.Single(t => t.Name == "t").Triggers.ShouldHaveSingleItem();
        introspected.Timing.ShouldBe(TriggerTiming.InsteadOf);
        introspected.Events.ShouldBe(TriggerEvent.Delete);
    }

    [Fact]
    public void BeforeTrigger_IsRejected()
    {
        // Arrange
        var action = new CreateTrigger(Obj("users"), new Trigger
        {
            Name = "t",
            Timing = TriggerTiming.Before,
            Events = TriggerEvent.Insert,
            Body = "BEGIN END",
        });

        // Act
        var result = _sut.Generate(action);

        // Assert — a facet SQL Server cannot express is an error diagnostic that blocks the plan, not an exception.
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("BEFORE");
    }

    // ── Comments, grants ──────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Comments_OnTableAndColumn()
    {
        await Exec($"CREATE TABLE [{_schema}].[users] (id int, email varchar(50))");

        await Apply(
            new SetTableComment(Obj("users"), null, "Registered users"),
            new SetColumnComment(Mem("users", "email"), null, "Login address"));

        var table = (await Introspect()).Tables.ShouldHaveSingleItem();
        table.Comment.ShouldBe("Registered users");
        table.Columns.Single(c => c.Name == "email").Comment.ShouldBe("Login address");

        // Update then clear the table comment.
        await Apply(new SetTableComment(Obj("users"), "Registered users", "All users"));
        (await Introspect()).Tables.ShouldHaveSingleItem().Comment.ShouldBe("All users");

        await Apply(new SetTableComment(Obj("users"), "All users", null));
        (await Introspect()).Tables.ShouldHaveSingleItem().Comment.ShouldBeNull();
    }

    [Fact]
    public async Task RoundTrip_TableGrant()
    {
        await Exec($"CREATE TABLE [{_schema}].[users] (id int)");
        await Exec($"CREATE ROLE [{_schema}_role]");

        await Apply(new GrantTablePrivileges(Obj("users"), $"{_schema}_role", TablePrivilege.Select | TablePrivilege.Insert));

        // Deterministic: the rendered GRANT lands in the catalog (SELECT + INSERT to the role).
        var granted = await ScalarInt($"""
            SELECT COUNT(*) FROM sys.database_permissions dp
            JOIN sys.database_principals pr ON pr.principal_id = dp.grantee_principal_id
            WHERE dp.major_id = OBJECT_ID(N'[{_schema}].[users]') AND dp.minor_id = 0 AND pr.name = '{_schema}_role'
              AND dp.state IN ('G', 'W') AND dp.permission_name IN ('SELECT', 'INSERT')
            """);
        granted.ShouldBe(2);

        // The introspector surfaces the grant. SQL Server can briefly hide a freshly-permissioned object from a new
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

    private async Task<NSchema.Model.Schemas.Schema> Introspect()
    {
        var database = await _introspector.GetDatabase(PlanningScope.To(new SchemaAddress(_schema)), TestContext.Current.CancellationToken);
        return database.Schemas.Single(s => s.Name == _schema);
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
