using Microsoft.Data.SqlClient;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.SqlServer.Sql;
using NSchema.SqlServer.Tests.Fixtures;

namespace NSchema.SqlServer.Tests.Sql;

[Collection("sqlserver")]
public sealed class SqlServerDatabaseIntrospectorTests(SqlServerContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = $"test_{Guid.NewGuid():N}";
    private SqlConnection _connection = null!;
    private SqlServerDatabaseIntrospector _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqlConnection(fixture.ConnectionString);
        await _connection.OpenAsync();
        _sut = new SqlServerDatabaseIntrospector(new SqlServerConnectionSource(fixture.ConnectionString));
        await Exec($"CREATE SCHEMA [{_schema}]");
    }

    public async ValueTask DisposeAsync() =>
        // The container is ephemeral and the schema name is unique per run, so no teardown is needed.
        await _connection.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Reads the live schema scoped to the given schema names.</summary>
    private async Task<Database> Introspect(params string[] schemas) =>
        (await _sut.GetDatabase(PlanningScope.To(schemas.Select(s => DatabaseAddress.Schema(s))), TestContext.Current.CancellationToken)).Require();

    /// <summary>Reads the live schema without a scope, so the engine's own schemas surface.</summary>
    private async Task<Database> IntrospectAll() =>
        (await _sut.GetDatabase(PlanningScope.All, TestContext.Current.CancellationToken)).Require();

    // ── Native types ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_Unscoped_CapturesTheEngineVocabulary()
    {
        // Act
        var model = await IntrospectAll();

        // Assert — sys surfaces as an implicit container holding the engine's types, spelled in the model's
        // canonical names: the same universe the column mapping produces.
        var sys = model.Schemas.Single(s => s.Name == "sys");
        sys.IsImplicit.ShouldBeTrue();
        var names = sys.NativeTypes.Select(t => t.Name.Value).ToHashSet();
        names.ShouldContain("guid");     // normalized from uniqueidentifier
        names.ShouldContain("double");   // normalized from float
        names.ShouldContain("decimal");  // normalized from numeric (and decimal itself)
        names.ShouldContain("datetime"); // normalized from datetime2
        names.ShouldContain("money");    // a built-in the model has no spelling for, verbatim
        names.ShouldNotContain("uniqueidentifier"); // the catalog spelling is folded away
        names.ShouldNotContain("datetime2");
        names.ShouldNotContain("bit");
    }

    [Fact]
    public async Task GetDatabase_UserDefinedType_KeepsItsSchema()
    {
        // Arrange — a UDT outside dbo used to lose its schema on introspection.
        await Exec($"CREATE TYPE [{_schema}].[money_amount] FROM decimal(19,4)");
        await Exec($"CREATE TABLE [{_schema}].[prices] (amount [{_schema}].[money_amount] NOT NULL)");

        // Act
        var model = await Introspect(_schema);

        // Assert — the column names its type in full, and the type itself joins the schema's vocabulary.
        var schema = model.Schemas.Single(s => s.Name == _schema);
        schema.Tables.Single(t => t.Name == "prices").Columns.ShouldHaveSingleItem()
            .Type.ShouldBe(SqlType.Custom(_schema, "money_amount"));
        schema.NativeTypes.ShouldHaveSingleItem().Name.ShouldBe("money_amount");
    }

    [Fact]
    public async Task GetDatabase_BuiltInOutsideTheModel_QualifiesWithSys()
    {
        // Arrange
        await Exec($"CREATE TABLE [{_schema}].[fees] (amount MONEY NOT NULL)");

        // Act
        var model = await Introspect(_schema);

        // Assert — captured verbatim with its real home; the equivalence folds sys away when comparing.
        model.Schemas.Single(s => s.Name == _schema).Tables.Single(t => t.Name == "fees")
            .Columns.ShouldHaveSingleItem().Type.ShouldBe(SqlType.Custom("sys", "money"));
    }

    [Fact]
    public async Task GetDatabase_TableType_IsNotVocabulary()
    {
        // Arrange — a table type cannot type a column, so it is not part of the vocabulary.
        await Exec($"CREATE TYPE [{_schema}].[id_list] AS TABLE (id INT NOT NULL)");

        // Act
        var model = await Introspect(_schema);

        // Assert
        model.Schemas.Single(s => s.Name == _schema).NativeTypes.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDatabase_ScopedRead_ExcludesTheEngineSchema()
    {
        // Act
        var model = await Introspect(_schema);

        // Assert — the vocabulary is filtered like everything else.
        model.Schemas.ShouldNotContain(s => s.Name == "sys");
    }
}
