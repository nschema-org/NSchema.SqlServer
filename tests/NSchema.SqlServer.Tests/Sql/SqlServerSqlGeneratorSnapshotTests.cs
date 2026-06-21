using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Sequence;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Views;
using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Constraints;
using NSchema.Schema.Model.Indexes;
using NSchema.Schema.Model.Routines;
using NSchema.Schema.Model.Scripts;
using NSchema.Schema.Model.Sequences;
using NSchema.Schema.Model.Tables;
using NSchema.Schema.Model.Views;
using NSchema.SqlServer.Sql;
using NSchema.Sql;

namespace NSchema.SqlServer.Tests.Sql;

/// <summary>
/// Snapshot tests for <see cref="SqlServerSqlGenerator"/>. Unlike <see cref="SqlServerSqlGeneratorTests"/>
/// (which executes generated DDL against a real database via Testcontainers), these assert on the exact T-SQL the
/// generator emits — no Docker required. Snapshots live alongside this file as <c>*.verified.txt</c>; review and
/// commit them when the generated SQL intentionally changes.
/// </summary>
public sealed class SqlServerSqlGeneratorSnapshotTests
{
    private static readonly ISqlGenerator Generator = new SqlServerSqlGenerator();

    private static Task VerifyPlan(params MigrationAction[] actions) =>
        Verify(Generator.Generate(new MigrationPlan(actions, [], [])));

    // ── Schema operations (SQL Server has no schema rename) ─────────────────────

    [Fact]
    public Task SchemaOperations() => VerifyPlan(
        new CreateSchema("sales"),
        new DropSchema("sales"));

    // ── Table operations ──────────────────────────────────────────────────────

    [Fact]
    public Task CreateTable_WithColumnsAndPrimaryKey() => VerifyPlan(
        new CreateTable("dbo", new Table("users",
            PrimaryKey: new PrimaryKey("pk_users", ["id"]),
            Columns:
            [
                new Column("id", SqlType.BigInt, IsNullable: false, IsIdentity: true),
                new Column("email", SqlType.VarChar(255), IsNullable: false),
                new Column("created_at", SqlType.DateTimeOffset, IsNullable: false, DefaultExpression: "sysdatetimeoffset()"),
                new Column("notes", SqlType.Text, IsNullable: true),
            ])));

    [Fact]
    public Task CreateTable_WithIdentityOptions() => VerifyPlan(
        new CreateTable("dbo", new Table("counters",
            Columns:
            [
                new Column("id", SqlType.BigInt, IsNullable: false, IsIdentity: true,
                    IdentityOptions: new IdentityOptions(StartWith: 1000, MinValue: null, IncrementBy: 5)),
            ])));

    [Fact]
    public Task TableLifecycle() => VerifyPlan(
        new RenameTable("dbo", "old_users", "users"),
        new DropTable("dbo", "legacy"));

    // ── Column operations ─────────────────────────────────────────────────────

    [Fact]
    public Task ColumnOperations() => VerifyPlan(
        new AddColumn("dbo", "users", new Column("age", SqlType.Int, IsNullable: true)),
        new RenameColumn("dbo", "users", "age", "years"),
        // A standalone type change restates the (unchanged) nullability the core supplies on the action.
        new AlterColumnType("dbo", "users", "years", SqlType.Int, SqlType.BigInt, IsNullable: true),
        // A standalone nullability change restates the (unchanged) type the core supplies on the action.
        new AlterColumnNullability("dbo", "users", "notes", OldNullable: true, NewNullable: false, ColumnType: SqlType.VarChar(200)),
        new SetColumnDefault("dbo", "users", "years", null, "0"),
        new SetColumnDefault("dbo", "users", "years", "0", null),
        new DropColumn("dbo", "users", new Column("years", SqlType.BigInt)));

    [Fact]
    public Task ColumnTypeAndNullability_FoldIntoOneAlterColumn() => VerifyPlan(
        // Both change for the same column: the type action emits the combined ALTER COLUMN (with the final
        // nullability) and the paired nullability action is folded away — one statement, not two.
        new AlterColumnType("dbo", "users", "age", SqlType.Int, SqlType.BigInt, IsNullable: false),
        new AlterColumnNullability("dbo", "users", "age", OldNullable: true, NewNullable: false, ColumnType: SqlType.BigInt));

    [Fact]
    public Task GeneratedColumnOperations() => VerifyPlan(
        new CreateTable("dbo", new Table("boxes",
            Columns:
            [
                new Column("w", SqlType.Int, IsNullable: false),
                new Column("h", SqlType.Int, IsNullable: false),
                new Column("area", SqlType.Int, GeneratedExpression: "w * h"),
            ])),
        new AddColumn("dbo", "boxes", new Column("perimeter", SqlType.Int, GeneratedExpression: "2 * (w + h)")));

    // ── Keys, indexes and constraints ───────────────────────────────────────────

    [Fact]
    public Task PrimaryKeyOperations() => VerifyPlan(
        new AddPrimaryKey("dbo", "users", new PrimaryKey("pk_users", ["id", "tenant_id"])),
        new DropPrimaryKey("dbo", "users", "pk_users"));

    [Fact]
    public Task ForeignKeyOperations() => VerifyPlan(
        new AddForeignKey("dbo", "orders", new ForeignKey(
            "fk_orders_user", ["user_id"], "dbo", "users", ["id"],
            OnDelete: ReferentialAction.Cascade, OnUpdate: ReferentialAction.SetNull)),
        new DropForeignKey("dbo", "orders", "fk_orders_user"));

    [Fact]
    public Task ConstraintOperations() => VerifyPlan(
        new AddUniqueConstraint("dbo", "users", new UniqueConstraint("uq_users_email", ["email"])),
        new DropUniqueConstraint("dbo", "users", "uq_users_email"),
        new AddCheckConstraint("dbo", "accounts", new CheckConstraint("ck_balance", "balance >= 0")),
        new DropCheckConstraint("dbo", "accounts", "ck_balance"));

    [Fact]
    public Task IndexOperations() => VerifyPlan(
        new CreateIndex("dbo", "users", new TableIndex("idx_users_email", ["email"], IsUnique: true)),
        new CreateIndex("dbo", "users", new TableIndex("idx_users_active", ["created_at"], Predicate: "notes IS NOT NULL")),
        // A covering INCLUDE plus a descending key.
        new CreateIndex("dbo", "users", new TableIndex("idx_users_recent",
            [new IndexColumn("created_at", Sort: IndexSort.Descending)], Include: ["id", "notes"])),
        new DropIndex("dbo", "users", "idx_users_email"));

    // ── Views (CREATE OR ALTER replaces in place) ────────────────────────────────

    [Fact]
    public Task ViewOperations() => VerifyPlan(
        new CreateView("dbo", new View("active_users", "SELECT id, email FROM dbo.users WHERE active = 1")),
        new RenameView("dbo", "legacy_active", "active_users"),
        new DropView("dbo", "active_users"));

    // ── Sequences ──────────────────────────────────────────────────────────────

    [Fact]
    public Task SequenceOperations() => VerifyPlan(
        new CreateSequence("dbo", new Sequence("order_id")),
        new CreateSequence("dbo", new Sequence("invoice_id", new SequenceOptions(
            SqlType.Int, StartWith: 100, IncrementBy: 5, MinValue: 10, MaxValue: 30000, Cache: 20, Cycle: true))),
        new RenameSequence("dbo", "bill_id", "invoice_id"),
        new AlterSequence("dbo", "invoice_id",
            OldOptions: new SequenceOptions(SqlType.Int, StartWith: 100, IncrementBy: 5, MinValue: 10, MaxValue: 30000, Cache: 20, Cycle: true),
            NewOptions: new SequenceOptions(SqlType.Int, IncrementBy: 50)),
        new DropSequence("dbo", "invoice_id"));

    // ── Routines (CREATE OR ALTER; a signature change recreates in place) ────────

    [Fact]
    public Task RoutineOperations() => VerifyPlan(
        new CreateRoutine("dbo", new Routine("active_user_count", RoutineKind.Function, "",
            "RETURNS int AS BEGIN RETURN (SELECT COUNT(*) FROM dbo.users WHERE active = 1) END")),
        new RenameRoutine("dbo", "user_count", "active_user_count", RoutineKind.Function),
        new RecreateRoutine("dbo", new Routine("add_numbers", RoutineKind.Function, "@a int, @b int",
            "RETURNS int AS BEGIN RETURN @a + @b END", Comment: "Adds numbers")),
        new DropRoutine("dbo", "active_user_count", RoutineKind.Function),
        new CreateRoutine("dbo", new Routine("archive_users", RoutineKind.Procedure, "@cutoff date",
            "AS BEGIN DELETE FROM dbo.users WHERE created_at < @cutoff END")),
        new DropRoutine("dbo", "archive_users", RoutineKind.Procedure));

    // ── Comments (extended properties: add / update / drop) ──────────────────────

    [Fact]
    public Task CommentOperations() => VerifyPlan(
        new SetSchemaComment("dbo", null, "Application schema"),
        new SetTableComment("dbo", "users", null, "Registered users"),
        new SetTableComment("dbo", "users", "Registered users", "All users"),
        new SetColumnComment("dbo", "users", "email", null, "Unique login address"),
        new SetIndexComment("dbo", "users", "idx_users_email", null, "Lookup by email"),
        new SetConstraintComment("dbo", "users", "uq_users_email", null, "One row per email"),
        new SetViewComment("dbo", "active_users", null, "Active users only"),
        new SetSequenceComment("dbo", "order_id", null, "Order numbers"),
        new SetRoutineComment("dbo", "add_numbers", null, "Adds numbers", RoutineKind.Function),
        new SetTableComment("dbo", "users", "All users", null));

    // ── Grants (table privileges) ───────────────────────────────────────────────

    [Fact]
    public Task GrantOperations() => VerifyPlan(
        new GrantTablePrivileges("dbo", "users", "app_role", TablePrivilege.Select | TablePrivilege.Insert),
        new GrantTablePrivileges("dbo", "users", "readonly", TablePrivilege.Select),
        new RevokeTablePrivileges("dbo", "users", "app_role", TablePrivilege.All));

    // ── Type mapping ─────────────────────────────────────────────────────────────

    [Fact]
    public Task TypeMapping_CoversAllSqlTypes() => VerifyPlan(
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

    private static AlterColumnType Alter(SqlType type) =>
        new("dbo", "t", "c", SqlType.Int, type, IsNullable: false);

    // ── Deployment scripts ──────────────────────────────────────────────────────

    [Fact]
    public void DeploymentScript_RunOutsideTransaction_IsCarriedOntoTheStatement()
    {
        var rebuild = new Script("reindex", "ALTER INDEX ALL ON dbo.users REBUILD", ScriptType.PostDeployment)
        {
            RunOutsideTransaction = true,
        };
        var ordinary = new Script("seed", "INSERT INTO dbo.users DEFAULT VALUES", ScriptType.PreDeployment);

        var plan = Generator.Generate(new MigrationPlan([], [ordinary], [rebuild]));

        plan.Statements.Single(s => s.Sql.Contains("INSERT")).RunOutsideTransaction.ShouldBeFalse();
        plan.Statements.Single(s => s.Sql.Contains("REBUILD")).RunOutsideTransaction.ShouldBeTrue();
    }
}
