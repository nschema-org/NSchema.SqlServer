using Microsoft.Data.SqlClient;
using NSchema.Configuration.Plugins;
using NSchema.Model;
using NSchema.Operations;
using NSchema.Plugins;
using NSchema.SqlServer.Sql;
using NSchema.SqlServer.Tests.Fixtures;

namespace NSchema.SqlServer.Tests;

/// <summary>
/// End-to-end proof that the <see cref="SqlServerPlugin"/> manifest wires a fully working provider: it runs a real
/// migration THROUGH the plugin's <c>Configure</c> (not the direct <c>UseSqlServer</c> API) against a real SQL
/// Server container, then re-introspects to confirm the schema was applied. Requires Docker.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerPluginEndToEndTests(SqlServerContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = $"e2e_{Guid.NewGuid():N}";
    private string _projectDir = null!;

    public ValueTask InitializeAsync()
    {
        _projectDir = Directory.CreateTempSubdirectory("nschema-mssql-e2e-").FullName;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // The container is ephemeral and the schema name is unique per run, so no teardown is needed (SQL Server's
        // DROP SCHEMA doesn't cascade anyway).
        Directory.Delete(_projectDir, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Apply_ThroughThePlugin_CreatesTheDesiredSchema()
    {
        // Arrange — a desired schema on disk, and a host configured ONLY through the plugin manifest.
        await File.WriteAllTextAsync(Path.Combine(_projectDir, "schema.sql"), $"""
            CREATE SCHEMA {_schema};

            CREATE TABLE {_schema}.widgets (
              id   int NOT NULL,
              name varchar(100),
              CONSTRAINT widgets_pkey PRIMARY KEY (id)
            );
            """, TestContext.Current.CancellationToken);

        var builder = NSchemaApplication.CreateBuilder();
        new SqlServerPlugin().Configure(builder, Config()).IsSuccess.ShouldBeTrue();

        builder.AddProjectSource(_projectDir);
        // Planning requires a state store; nothing here needs to outlive the app, so an in-memory one suffices.
        builder.UseEphemeralState();
        using var app = builder.Build();

        // Act — a real plan + apply through the plugin-wired provider. Nothing is on record, so the whole declared
        // schema plans as an add.
        var planResult = await app.Operations.Plan(new PlanArguments { Scope = Scope() }, TestContext.Current.CancellationToken);
        planResult.IsSuccess.ShouldBeTrue();
        var applyResult = await app.Operations.Apply(new ApplyArguments { Plan = planResult.Value.Plan! }, TestContext.Current.CancellationToken);
        applyResult.IsSuccess.ShouldBeTrue();

        // Assert — the table really exists, read back via a fresh introspection.
        var live = await new SqlServerDatabaseIntrospector(new SqlServerConnectionSource(fixture.ConnectionString))
            .GetDatabase(Scope(), TestContext.Current.CancellationToken);
        var table = live.Schemas.ShouldHaveSingleItem().Tables.ShouldHaveSingleItem();
        table.Name.ShouldBe("widgets");
        table.Columns.Select(column => column.Name).ShouldBe([new SqlIdentifier("id"), new SqlIdentifier("name")]);
    }

    [Fact]
    public async Task Apply_WithChangeScript_BackfillsANewNotNullColumnOnAPopulatedTable()
    {
        // Arrange — a live baseline table that already holds a row, created out of band.
        await using var conn = new SqlConnection(fixture.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await Exec(conn, $"CREATE SCHEMA [{_schema}]");
        await Exec(conn, $"CREATE TABLE [{_schema}].[users] (id int NOT NULL, login varchar(50) NOT NULL, CONSTRAINT users_pkey PRIMARY KEY (id))");
        await Exec(conn, $"INSERT INTO [{_schema}].[users] (id, login) VALUES (1, 'alice')");

        // The desired schema adds a NOT NULL column with no default — only possible on a populated table because
        // the matched change script backfills it (the core plans: add nullable → run the SQL → tighten to NOT NULL).
        await File.WriteAllTextAsync(Path.Combine(_projectDir, "schema.sql"), $"""
            CREATE SCHEMA {_schema};

            CREATE TABLE {_schema}.users (
              id    int NOT NULL,
              login varchar(50) NOT NULL,
              email varchar(100) NOT NULL,
              CONSTRAINT users_pkey PRIMARY KEY (id)
            );

            SCRIPT backfill RUN ON ADD COLUMN {_schema}.users.email AS $$
              UPDATE {_schema}.users SET email = login + '@example.com' WHERE email IS NULL;
            $$;
            """, TestContext.Current.CancellationToken);

        var builder = NSchemaApplication.CreateBuilder();
        new SqlServerPlugin().Configure(builder, Config()).IsSuccess.ShouldBeTrue();

        builder.AddProjectSource(_projectDir);
        builder.UseEphemeralState();
        using var app = builder.Build();

        // Act — a plan diffs recorded state against the project, so the refresh is what puts the live baseline on
        // record (and makes the new column an ADD COLUMN the script can attach to).
        (await app.Operations.Refresh(new RefreshArguments(), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var planResult = await app.Operations.Plan(new PlanArguments { Scope = Scope() }, TestContext.Current.CancellationToken);
        planResult.IsSuccess.ShouldBeTrue();
        var applyResult = await app.Operations.Apply(new ApplyArguments { Plan = planResult.Value.Plan! }, TestContext.Current.CancellationToken);
        applyResult.IsSuccess.ShouldBeTrue();

        // Assert — the column ended NOT NULL and the existing row was backfilled by the script's SQL.
        (await Scalar(conn, $"SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID(N'[{_schema}].[users]') AND name = 'email'"))
            .ShouldBe(false);
        (await Scalar(conn, $"SELECT email FROM [{_schema}].[users] WHERE id = 1")).ShouldBe("alice@example.com");
    }

    private PlanningScope Scope() => PlanningScope.To(new SqlIdentifier(_schema));

    private PluginSettings Config() => new(new PluginLabel("sqlserver"), new Dictionary<string, string?>
    {
        ["connection_string"] = fixture.ConnectionString,
    });

    private static async Task Exec(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<object?> Scalar(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
