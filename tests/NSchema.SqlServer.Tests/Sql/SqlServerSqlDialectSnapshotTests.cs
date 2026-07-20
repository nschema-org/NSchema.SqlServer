using NSchema.Diff.Model;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Constraints;
using NSchema.Model.Indexes;
using NSchema.Model.Routines;
using NSchema.Model.Scripts;
using NSchema.Model.Sequences;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Model.Views;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Scripts;
using NSchema.Plan.Model.Sequences;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.SqlServer.Sql;

namespace NSchema.SqlServer.Tests.Sql;

/// <summary>
/// Snapshot tests for <see cref="SqlServerSqlDialect"/>. Unlike <see cref="SqlServerSqlDialectTests"/>
/// (which executes rendered DDL against a real database via Testcontainers), these assert on the exact T-SQL the
/// dialect emits — no Docker required. Snapshots live alongside this file as <c>*.verified.txt</c>; review and
/// commit them when the rendered SQL intentionally changes.
/// </summary>
public sealed class SqlServerSqlDialectSnapshotTests
{
    private readonly SqlServerSqlDialect _sut = new();

    private Task VerifyStatements(params MigrationAction[] actions) =>
        Verify(actions.SelectMany(action => _sut.Generate(action).Require()).ToList());

    // ── Schema operations (SQL Server has no schema rename) ─────────────────────

    [Fact]
    public Task SchemaOperations() => VerifyStatements(
        new CreateSchema("sales"),
        new DropSchema("sales"));

    // ── Table operations ──────────────────────────────────────────────────────

    [Fact]
    public Task CreateTable_WithColumnsAndPrimaryKey() => VerifyStatements(
        new CreateTable("dbo", new Table
        {
            Name = "users",
            PrimaryKey = new PrimaryKey { Name = "pk_users", ColumnNames = [new("id")] },
            Columns =
            [
                new Column { Name = "id", Type = SqlType.BigInt, IsNullable = false, IsIdentity = true },
                new Column { Name = "email", Type = SqlType.VarChar(255), IsNullable = false },
                new Column { Name = "created_at", Type = SqlType.DateTimeOffset, IsNullable = false, DefaultExpression = "sysdatetimeoffset()" },
                new Column { Name = "notes", Type = SqlType.Text, IsNullable = true },
            ],
        }));

    [Fact]
    public Task CreateTable_WithIdentityOptions() => VerifyStatements(
        new CreateTable("dbo", new Table
        {
            Name = "counters",
            Columns =
            [
                new Column
                {
                    Name = "id", Type = SqlType.BigInt, IsIdentity = true,
                    IdentityOptions = new IdentityOptions(StartWith: 1000, MinValue: null, IncrementBy: 5),
                },
            ],
        }));

    [Fact]
    public Task CreateTable_WithInlineConstraints() => VerifyStatements(
        // A newly-created table carries every constraint inline: primary key, unique, check and foreign key — the
        // linearizer folds these into CREATE TABLE rather than emitting separate ALTER TABLE adds.
        new CreateTable("dbo", new Table
        {
            Name = "orders",
            PrimaryKey = new PrimaryKey { Name = "pk_orders", ColumnNames = [new("id")] },
            Columns =
            [
                new Column { Name = "id", Type = SqlType.BigInt, IsNullable = false, IsIdentity = true },
                new Column { Name = "user_id", Type = SqlType.BigInt, IsNullable = false },
                new Column { Name = "code", Type = SqlType.VarChar(20), IsNullable = false },
                new Column { Name = "total", Type = SqlType.Int, IsNullable = false },
            ],
            UniqueConstraints = [new UniqueConstraint { Name = "uq_orders_code", ColumnNames = [new("code")] }],
            CheckConstraints = [new CheckConstraint { Name = "ck_orders_total", Expression = "total >= 0" }],
            ForeignKeys =
            [
                new ForeignKey
                {
                    Name = "fk_orders_user",
                    ColumnNames = [new("user_id")],
                    References = new ObjectAddress("dbo", "users"),
                    ReferencedColumnNames = [new("id")],
                    OnDelete = ReferentialAction.Cascade,
                },
            ],
        }));

    [Fact]
    public Task TableLifecycle() => VerifyStatements(
        new RenameTable(new ObjectAddress("dbo", "old_users"), "users"),
        new DropTable(new ObjectAddress("dbo", "legacy")));

    // ── Column operations ─────────────────────────────────────────────────────

    [Fact]
    public Task ColumnOperations() => VerifyStatements(
        new AddColumn(new ObjectAddress("dbo", "users"), new Column { Name = "age", Type = SqlType.Int, IsNullable = true }),
        new RenameColumn(new MemberAddress("dbo", "users", "age"), "years"),
        new AlterColumn(new ObjectAddress("dbo", "users"), new Column { Name = "years", Type = SqlType.BigInt, IsNullable = true }, Type: new(SqlType.Int, SqlType.BigInt)),
        new AlterColumn(new ObjectAddress("dbo", "users"), new Column { Name = "notes", Type = SqlType.VarChar(200) }, Nullability: new(true, false)),
        new SetColumnDefault(new MemberAddress("dbo", "users", "years"), null, "0"),
        new SetColumnDefault(new MemberAddress("dbo", "users", "years"), "0", null),
        new DropColumn(new ObjectAddress("dbo", "users"), new Column { Name = "years", Type = SqlType.BigInt }));

    [Fact]
    public Task ColumnTypeAndNullability_RenderAsOneStatement() => VerifyStatements(
        new AlterColumn(new ObjectAddress("dbo", "users"), new Column { Name = "age", Type = SqlType.BigInt }, Type: new(SqlType.Int, SqlType.BigInt), Nullability: new(true, false)));

    [Fact]
    public Task GeneratedColumnOperations() => VerifyStatements(
        new CreateTable("dbo", new Table
        {
            Name = "boxes",
            Columns =
            [
                new Column { Name = "w", Type = SqlType.Int },
                new Column { Name = "h", Type = SqlType.Int },
                new Column { Name = "area", Type = SqlType.Int, GeneratedExpression = "w * h" },
            ],
        }),
        new AddColumn(new ObjectAddress("dbo", "boxes"), new Column { Name = "perimeter", Type = SqlType.Int, GeneratedExpression = "2 * (w + h)" }));

    // ── Keys, indexes and constraints ───────────────────────────────────────────

    [Fact]
    public Task PrimaryKeyOperations() => VerifyStatements(
        new AddPrimaryKey(new ObjectAddress("dbo", "users"), new PrimaryKey { Name = "pk_users", ColumnNames = [new("id"), new("tenant_id")] }),
        new DropPrimaryKey(new MemberAddress("dbo", "users", "pk_users")));

    [Fact]
    public Task ForeignKeyOperations() => VerifyStatements(
        new AddForeignKey(new ObjectAddress("dbo", "orders"), new ForeignKey
        {
            Name = "fk_orders_user",
            ColumnNames = [new("user_id")],
            References = new ObjectAddress("dbo", "users"),
            ReferencedColumnNames = [new("id")],
            OnDelete = ReferentialAction.Cascade,
            OnUpdate = ReferentialAction.SetNull,
        }),
        new DropForeignKey(new MemberAddress("dbo", "orders", "fk_orders_user")));

    [Fact]
    public Task ConstraintOperations() => VerifyStatements(
        new AddUniqueConstraint(new ObjectAddress("dbo", "users"), new UniqueConstraint { Name = "uq_users_email", ColumnNames = [new("email")] }),
        new DropUniqueConstraint(new MemberAddress("dbo", "users", "uq_users_email")),
        new AddCheckConstraint(new ObjectAddress("dbo", "accounts"), new CheckConstraint { Name = "ck_balance", Expression = "balance >= 0" }),
        new DropCheckConstraint(new MemberAddress("dbo", "accounts", "ck_balance")));

    [Fact]
    public Task IndexOperations() => VerifyStatements(
        new CreateIndex(new ObjectAddress("dbo", "users"), new TableIndex { Name = "idx_users_email", Columns = ["email"], IsUnique = true }),
        new CreateIndex(new ObjectAddress("dbo", "users"), new TableIndex { Name = "idx_users_active", Columns = ["created_at"], Predicate = "notes IS NOT NULL" }),
        // A covering INCLUDE plus a descending key.
        new CreateIndex(new ObjectAddress("dbo", "users"), new TableIndex
        {
            Name = "idx_users_recent",
            Columns = [new IndexColumn(new SqlIdentifier("created_at"), Sort: IndexSort.Descending)],
            Include = [new("id"), new("notes")],
        }),
        new DropIndex(new MemberAddress("dbo", "users", "idx_users_email")));

    // ── Triggers (inline body; CREATE OR ALTER) ──────────────────────────────────

    [Fact]
    public Task TriggerOperations() => VerifyStatements(
        new CreateTrigger(new ObjectAddress("dbo", "users"), new Trigger
        {
            Name = "users_audit",
            Timing = TriggerTiming.After,
            Events = TriggerEvent.Insert | TriggerEvent.Update,
            Body = "BEGIN INSERT INTO dbo.audit (msg) VALUES ('changed'); END",
        }),
        new CreateTrigger(new ObjectAddress("dbo", "invoices"), new Trigger
        {
            Name = "invoices_guard",
            Timing = TriggerTiming.InsteadOf,
            Events = TriggerEvent.Delete,
            Body = "BEGIN RAISERROR('no deletes', 16, 1); END",
        }),
        new SetTriggerComment(new MemberAddress("dbo", "users", "users_audit"), null, "audit changes"),
        new DropTrigger(new MemberAddress("dbo", "users", "users_audit")));

    // ── Views (CREATE OR ALTER replaces in place) ────────────────────────────────

    [Fact]
    public Task ViewOperations() => VerifyStatements(
        new CreateView("dbo", new View { Name = "active_users", Body = "SELECT id, email FROM dbo.users WHERE active = 1" }),
        new RenameView(new ObjectAddress("dbo", "legacy_active"), "active_users"),
        new DropView(new ObjectAddress("dbo", "active_users")));

    // ── Sequences ──────────────────────────────────────────────────────────────

    [Fact]
    public Task SequenceOperations() => VerifyStatements(
        new CreateSequence("dbo", new Sequence { Name = "order_id" }),
        new CreateSequence("dbo", new Sequence
        {
            Name = "invoice_id",
            Options = new SequenceOptions(SqlType.Int, StartWith: 100, IncrementBy: 5, MinValue: 10, MaxValue: 30000, Cache: 20, Cycle: true),
        }),
        new RenameSequence(new ObjectAddress("dbo", "bill_id"), "invoice_id"),
        new AlterSequence(new ObjectAddress("dbo", "invoice_id"),
            OldOptions: new SequenceOptions(SqlType.Int, StartWith: 100, IncrementBy: 5, MinValue: 10, MaxValue: 30000, Cache: 20, Cycle: true),
            NewOptions: new SequenceOptions(SqlType.Int, IncrementBy: 50)),
        new DropSequence(new ObjectAddress("dbo", "invoice_id")));

    // ── Routines (CREATE OR ALTER; a signature change recreates in place) ────────

    [Fact]
    public Task RoutineOperations() => VerifyStatements(
        new CreateRoutine("dbo", new Routine
        {
            Name = "active_user_count",
            RoutineKind = RoutineKind.Function,
            Arguments = "",
            Definition = "RETURNS int AS BEGIN RETURN (SELECT COUNT(*) FROM dbo.users WHERE active = 1) END",
        }),
        new RenameRoutine(new ObjectAddress("dbo", "user_count"), "active_user_count", RoutineKind.Function),
        new RecreateRoutine("dbo", new Routine
        {
            Name = "add_numbers",
            RoutineKind = RoutineKind.Function,
            Arguments = "@a int, @b int",
            Definition = "RETURNS int AS BEGIN RETURN @a + @b END",
            Comment = "Adds numbers",
        }),
        new DropRoutine(new ObjectAddress("dbo", "active_user_count"), RoutineKind.Function),
        new CreateRoutine("dbo", new Routine
        {
            Name = "archive_users",
            RoutineKind = RoutineKind.Procedure,
            Arguments = "@cutoff date",
            Definition = "AS BEGIN DELETE FROM dbo.users WHERE created_at < @cutoff END",
        }),
        new DropRoutine(new ObjectAddress("dbo", "archive_users"), RoutineKind.Procedure));

    // ── Comments (extended properties: add / update / drop) ──────────────────────

    [Fact]
    public Task CommentOperations() => VerifyStatements(
        new SetSchemaComment("dbo", null, "Application schema"),
        new SetTableComment(new ObjectAddress("dbo", "users"), null, "Registered users"),
        new SetTableComment(new ObjectAddress("dbo", "users"), "Registered users", "All users"),
        new SetColumnComment(new MemberAddress("dbo", "users", "email"), null, "Unique login address"),
        new SetIndexComment(new MemberAddress("dbo", "users", "idx_users_email"), null, "Lookup by email"),
        new SetConstraintComment(new MemberAddress("dbo", "users", "uq_users_email"), null, "One row per email"),
        new SetViewComment(new ObjectAddress("dbo", "active_users"), null, "Active users only"),
        new SetSequenceComment(new ObjectAddress("dbo", "order_id"), null, "Order numbers"),
        new SetRoutineComment(new ObjectAddress("dbo", "add_numbers"), null, "Adds numbers", RoutineKind.Function),
        new SetTableComment(new ObjectAddress("dbo", "users"), "All users", null));

    // ── Grants (table privileges) ───────────────────────────────────────────────

    [Fact]
    public Task GrantOperations() => VerifyStatements(
        new GrantTablePrivileges(new ObjectAddress("dbo", "users"), "app_role", TablePrivilege.Select | TablePrivilege.Insert),
        new GrantTablePrivileges(new ObjectAddress("dbo", "users"), "readonly", TablePrivilege.Select),
        new RevokeTablePrivileges(new ObjectAddress("dbo", "users"), "app_role", TablePrivilege.All));

    // ── Type mapping ─────────────────────────────────────────────────────────────

    [Fact]
    public Task TypeMapping_CoversAllSqlTypes() => VerifyStatements(
        Alter(SqlType.Boolean),
        Alter(SqlType.TinyInt),
        Alter(SqlType.SmallInt),
        Alter(SqlType.Int),
        Alter(SqlType.BigInt),
        Alter(SqlType.Float),
        Alter(SqlType.Double),
        Alter(SqlType.Decimal(18, 4)),
        Alter(SqlType.Char(10)),
        Alter(SqlType.NChar(10)),
        Alter(SqlType.VarChar(null)),
        Alter(SqlType.VarChar(100)),
        Alter(SqlType.NVarChar(null)),
        Alter(SqlType.NVarChar(100)),
        Alter(SqlType.Text),
        Alter(SqlType.Date),
        Alter(SqlType.Time),
        Alter(SqlType.DateTime),
        Alter(SqlType.DateTimeOffset),
        Alter(SqlType.Guid),
        Alter(SqlType.Binary(16)),
        Alter(SqlType.VarBinary(null)),
        Alter(SqlType.Custom("money")));

    private static AlterColumn Alter(SqlType type) =>
        new(new ObjectAddress("dbo", "t"), new Column { Name = "c", Type = type }, Type: new(SqlType.Int, type));

    // ── Unsupported surfaces (error diagnostics, not exceptions) ─────────────────

    [Fact]
    public void RenameSchema_IsAnErrorDiagnostic()
    {
        // Act
        var result = _sut.Generate(new RenameSchema("old_sales", "sales"));

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("rename schema");
    }

    // ── Scripts (user-authored SQL, passed through verbatim) ─────────────────────

    [Fact]
    public Task ScriptOperations() => VerifyStatements(
        new ExecuteScript(new ChangeScript("backfill_emails", "UPDATE dbo.users SET email = login + '@example.com' WHERE email IS NULL",
            new ChangeTarget("dbo", "users", "email", ChangeTrigger.AddColumn))),
        new ExecuteScript(new DeploymentScript("reindex", "ALTER INDEX ALL ON dbo.users REBUILD", null, DeploymentPhase.Post)
        {
            RunOutsideTransaction = true,
        }));

    [Fact]
    public void Script_EmitsTheSqlVerbatim()
    {
        // Arrange — the SQL is user-authored T-SQL: no bracket quoting, no rewriting.
        const string sql = "UPDATE dbo.users SET email = lower(login) WHERE email IS NULL";

        // Act
        var statements = _sut.Generate(new ExecuteScript(Script(sql))).Require();

        // Assert
        statements.ShouldHaveSingleItem().Sql.ShouldBe(sql);
    }

    [Fact]
    public void Script_RunOutsideTransaction_IsCarriedOntoTheStatement()
    {
        // Act
        var outside = _sut.Generate(new ExecuteScript(Script("UPDATE dbo.users SET a = 1") with { RunOutsideTransaction = true })).Require();
        var inside = _sut.Generate(new ExecuteScript(Script("UPDATE dbo.users SET b = 2"))).Require();

        // Assert
        outside.ShouldHaveSingleItem().RunOutsideTransaction.ShouldBeTrue();
        inside.ShouldHaveSingleItem().RunOutsideTransaction.ShouldBeFalse();
    }

    private static ChangeScript Script(string sql) =>
        new("backfill", sql, new ChangeTarget("dbo", "users", "email", ChangeTrigger.AddColumn));
}
