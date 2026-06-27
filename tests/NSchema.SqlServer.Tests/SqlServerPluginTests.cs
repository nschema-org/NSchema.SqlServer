using NSchema.Configuration;
using NSchema.Plugins;
using NSchema.Sql;

namespace NSchema.SqlServer.Tests;

/// <summary>
/// Pins <see cref="SqlServerPlugin"/>'s block parsing, environment-override precedence, and validation. Pure
/// unit tests — no Docker. The <c>NSCHEMA_SQLSERVER_*</c> variables are snapshotted and cleared so a
/// developer's ambient environment cannot make the outcome non-deterministic.
/// </summary>
public sealed class SqlServerPluginTests : IDisposable
{
    private static readonly string[] EnvVars =
    [
        "NSCHEMA_SQLSERVER_CONNECTION_STRING",
        "NSCHEMA_SQLSERVER_USERNAME",
        "NSCHEMA_SQLSERVER_PASSWORD",
    ];

    private readonly Dictionary<string, string?> _savedEnv = new();
    private readonly SqlServerPlugin _sut = new();

    public SqlServerPluginTests()
    {
        foreach (var name in EnvVars)
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void Label_IsSqlServer() => _sut.Label.ShouldBe("sqlserver");

    [Fact]
    public void GetScaffoldTemplate_ReturnsProviderBlock()
        => _sut.GetScaffoldTemplate(new ScaffoldContext()).ShouldContain("PROVIDER sqlserver");

    [Fact]
    public void GetScaffoldTemplate_WithVersion_PinsIt()
        => _sut.GetScaffoldTemplate(new ScaffoldContext { Version = "9.9.9" }).ShouldContain("version           = '9.9.9',");

    [Fact]
    public void GetSampleSchema_ScaffoldsANamedSchema()
    {
        var schema = _sut.GetSampleSchema();

        schema.ShouldContain("CREATE SCHEMA app;");
        schema.ShouldContain("CREATE TABLE app.widgets");
    }

    [Fact]
    public void Configure_ValidConnectionString_SucceedsAndRegistersProvider()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block(("connection_string", ConfigValue.OfString("Server=localhost;Database=app")));

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        builder.Services.ShouldContain(d => d.ServiceType == typeof(ISqlGenerator));
    }

    [Fact]
    public void Configure_MissingConnectionString_FailsWithRequiredError()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block();

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("connection_string is required"));
    }

    [Fact]
    public void Configure_UnknownAttribute_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block(
            ("connection_string", ConfigValue.OfString("Server=localhost")),
            ("nonsense", ConfigValue.OfString("x")));

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("unknown attribute 'nonsense'"));
    }

    [Fact]
    public void Configure_NonIntegerCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block(
            ("connection_string", ConfigValue.OfString("Server=localhost")),
            ("command_timeout", ConfigValue.OfString("soon")));

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("command_timeout must be an integer"));
    }

    [Fact]
    public void Configure_NegativeCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block(
            ("connection_string", ConfigValue.OfString("Server=localhost")),
            ("command_timeout", ConfigValue.OfInteger(-1)));

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("command_timeout must not be negative"));
    }

    [Fact]
    public void Configure_MultipleProblems_AggregatesEveryError()
    {
        // Arrange — an unknown attribute and no connection string: both must be reported, not just the first.
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block(("nope", ConfigValue.OfString("x")));

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.Count.ShouldBe(2);
    }

    [Fact]
    public void Configure_EnvironmentConnectionString_SatisfiesOmittedBlockAttribute()
    {
        // Arrange
        Environment.SetEnvironmentVariable("NSCHEMA_SQLSERVER_CONNECTION_STRING", "Server=env-host;Database=app");
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block();

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    private static ConfigBlock Block(params (string Key, ConfigValue Value)[] attributes)
        => new("provider", "sqlserver", attributes.ToDictionary(a => a.Key, a => a.Value));
}
