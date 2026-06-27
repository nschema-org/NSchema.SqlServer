using NSchema.Configuration;
using NSchema.Operations.Apply;
using NSchema.SqlServer.Sql;
using NSchema.SqlServer.Tests.Fixtures;

namespace NSchema.SqlServer.Tests;

/// <summary>
/// End-to-end proof that the <see cref="SqlServerPlugin"/> manifest wires a fully working provider: it runs a real
/// migration THROUGH the plugin's <c>Configure</c> (not the direct <c>UseSqlServerSchema</c> API) against a real SQL
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

        var builder = NSchemaApplication.CreateBuilder(new NSchemaApplicationOptions { ExceptionBehavior = ExceptionBehavior.Throw });
        var configured = new SqlServerPlugin().Configure(builder, new ConfigBlock("provider", "sqlserver", new Dictionary<string, ConfigValue>
        {
            ["connection_string"] = ConfigValue.OfString(fixture.ConnectionString),
        }));
        configured.Succeeded.ShouldBeTrue();

        builder.AddDdlSchemas(_projectDir);
        using var app = builder.Build();

        // Act — a real apply through the plugin-wired provider.
        await app.Apply(new ApplyArguments { Schemas = [_schema] }, TestContext.Current.CancellationToken);

        // Assert — the table really exists, read back via a fresh introspection.
        var live = await new SqlServerSchemaProvider(new SqlServerConnectionSource(fixture.ConnectionString))
            .GetSchema([_schema], TestContext.Current.CancellationToken);
        var table = live.Schemas.ShouldHaveSingleItem().Tables.ShouldHaveSingleItem();
        table.Name.ShouldBe("widgets");
        table.Columns.Select(column => column.Name).ShouldBe(["id", "name"]);
    }
}
