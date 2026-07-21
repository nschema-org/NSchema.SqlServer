using NSchema.Plan.Backends;
using NSchema.Plugins;
using NSchema.Plugins.Model;
using NSchema.Plugins.Model.Config;

namespace NSchema.SqlServer.Tests;

/// <summary>
/// Pins <see cref="SqlServerPlugin"/>'s configuration binding, environment-override precedence, and validation. Pure
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
    public void GetScaffoldTemplate_ReturnsDatabaseStatement()
        => _sut.GetScaffoldTemplate(new ScaffoldContext()).ShouldContain("DATABASE sqlserver");

    [Fact]
    public void GetSampleSchema_ScaffoldsANamedSchema()
    {
        var schema = _sut.GetSampleSchema();

        schema.ShouldContain("CREATE SCHEMA app;");
        schema.ShouldContain("CREATE TABLE app.widgets");
    }

    [Fact]
    public void Configure_ValidConnectionString_SucceedsAndRegistersDialect()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("connection_string", ConfigValue.OfString("Server=localhost;Database=app")));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        builder.Services.ShouldContain(d => d.ServiceType == typeof(SqlDialect));
    }

    [Fact]
    public void Configure_MissingConnectionString_FailsWithRequiredError()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config();

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("requires connection_string"));
    }

    [Fact]
    public void Configure_UnknownAttribute_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", ConfigValue.OfString("Server=localhost")),
            ("nonsense", ConfigValue.OfString("x")));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("nonsense"));
    }

    [Fact]
    public void Configure_NonIntegerCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", ConfigValue.OfString("Server=localhost")),
            ("command_timeout", ConfigValue.OfString("soon")));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("command_timeout"));
    }

    [Fact]
    public void Configure_NegativeCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", ConfigValue.OfString("Server=localhost")),
            ("command_timeout", ConfigValue.OfInteger(-1)));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("must not be negative"));
    }

    [Fact]
    public void Configure_MultipleProblems_AggregatesEveryError()
    {
        // Arrange — an unknown attribute and no connection string: both must be reported, not just the first.
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("nope", ConfigValue.OfString("x")));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.Count().ShouldBe(2);
    }

    [Fact]
    public void Configure_EnvironmentConnectionString_SatisfiesOmittedAttribute()
    {
        // Arrange
        Environment.SetEnvironmentVariable("NSCHEMA_SQLSERVER_CONNECTION_STRING", "Server=env-host;Database=app");
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config();

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    private static PluginConfig Config(params (string Key, ConfigValue Value)[] attributes)
        => new(new PluginLabel("sqlserver"), attributes.ToDictionary(a => new AttributeKey(a.Key), a => a.Value));
}
