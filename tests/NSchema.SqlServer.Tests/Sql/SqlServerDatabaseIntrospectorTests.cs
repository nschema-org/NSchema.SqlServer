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
        names.ShouldContain("datetime");  // the legacy type, verbatim
        names.ShouldContain("datetime2"); // the modern type, verbatim — two real types, never conflated
        names.ShouldContain("money");    // a built-in the model has no spelling for, verbatim
        names.ShouldNotContain("uniqueidentifier"); // the catalog spelling is folded away
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

        // Assert — the column names its type in full, and the type itself is a domain declaration.
        var schema = model.Schemas.Single(s => s.Name == _schema);
        schema.Tables.Single(t => t.Name == "prices").Columns.ShouldHaveSingleItem()
            .Type.ShouldBe(SqlType.Custom(_schema, "money_amount"));
        schema.Domains.ShouldHaveSingleItem().Name.ShouldBe("money_amount");
    }

    [Fact]
    public async Task GetDatabase_BuiltInOutsideTheModel_CapturesBare()
    {
        // Arrange
        await Exec($"CREATE TABLE [{_schema}].[fees] (amount MONEY NOT NULL)");

        // Act
        var model = await Introspect(_schema);

        // Assert — the engine's own vocabulary is addressed and written bare; only a user-defined type
        // carries its owning schema.
        model.Schemas.Single(s => s.Name == _schema).Tables.Single(t => t.Name == "fees")
            .Columns.ShouldHaveSingleItem().Type.ShouldBe(SqlType.Custom("money"));
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

    // ── Domains (alias types) ─────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_AliasType_IsADomain()
    {
        // Arrange
        await Exec($"CREATE TYPE [{_schema}].[OrderNumber] FROM [nvarchar](25) NULL");
        await Exec($"CREATE TYPE [{_schema}].[Flag] FROM [bit] NOT NULL");
        await Exec($"EXECUTE sys.sp_addextendedproperty N'MS_Description', N'A yes/no flag.', N'SCHEMA', [{_schema}], N'TYPE', [Flag]");
        await Exec($"CREATE TABLE [{_schema}].[orders] (num [{_schema}].[OrderNumber] NULL)");

        // Act
        var model = await Introspect(_schema);

        // Assert
        var schema = model.Schemas.Single(s => s.Name == _schema);
        var flag = schema.Domains.Single(d => d.Name == "Flag");
        flag.DataType.ShouldBe(SqlType.Boolean);
        flag.NotNull.ShouldBeTrue();
        flag.Comment.ShouldBe("A yes/no flag.");
        var orderNumber = schema.Domains.Single(d => d.Name == "OrderNumber");
        orderNumber.DataType.ShouldBe(SqlType.NVarChar(25));
        orderNumber.NotNull.ShouldBeFalse();
        schema.NativeTypes.ShouldBeEmpty(); // an alias type is a declaration now, not vocabulary
        schema.Tables.ShouldHaveSingleItem().Columns.ShouldHaveSingleItem()
            .Type.ShouldBe(SqlType.Custom(_schema, "OrderNumber"));
    }

    // ── Routines ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_UnparenthesisedProcedure_SplitsArgumentsAtTheHeaderAs()
    {
        // Arrange — T-SQL style: no parentheses, newline-delimited AS, a parameter comment, and a body whose
        // own AS keywords (CTE, aliases) must not end the header early.
        await Exec($"""
            CREATE PROCEDURE [{_schema}].[bom]
                @StartID [int], -- the root assembly
                @Depth [int]
            AS
            BEGIN
                SET NOCOUNT ON;
                WITH [cte]([id]) -- CTE name and columns
                AS (
                    SELECT [object_id] AS [id] FROM [sys].[objects]
                )
                SELECT [id] FROM [cte]
            END;
            """);

        // Act
        var model = await Introspect(_schema);

        // Assert
        var routine = model.Schemas.Single(s => s.Name == _schema).Routines.ShouldHaveSingleItem();
        routine.Arguments.Value.ShouldBe("@StartID [int], -- the root assembly\n    @Depth [int]");
        routine.Definition.Value.ShouldStartWith("AS");
        routine.Definition.Value.ShouldContain("SET NOCOUNT ON;");
    }

    [Fact]
    public async Task GetDatabase_CommentWithTrailingWhitespace_IsTrimmed()
    {
        // Arrange — an NSQL doc comment cannot express surrounding whitespace, so it must not survive introspection.
        await Exec($"CREATE TABLE [{_schema}].[t] (id int)");
        await Exec($"EXECUTE sys.sp_addextendedproperty N'MS_Description', N'Padded description. ', N'SCHEMA', [{_schema}], N'TABLE', [t]");

        // Act
        var model = await Introspect(_schema);

        // Assert
        model.Schemas.Single(s => s.Name == _schema)
            .Tables.ShouldHaveSingleItem().Comment.ShouldBe("Padded description.");
    }

    // ── Views ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_ViewWithTrailingSemicolon_BodyIsTheBareQuery()
    {
        // Arrange — sys.sql_modules stores the CREATE statement as written, terminator included.
        await Exec($"CREATE VIEW [{_schema}].[ones] AS SELECT 1 AS one;");

        // Act
        var model = await Introspect(_schema);

        // Assert
        model.Schemas.Single(s => s.Name == _schema)
            .Views.ShouldHaveSingleItem().Body.Value.ShouldBe("SELECT 1 AS one");
    }
}
