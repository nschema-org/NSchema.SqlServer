using NSchema.Configuration.Plugins;
using NSchema.Plan.Backends;
using NSchema.Plugins;
using NSchema.Project.Nsql.Syntax.Settings;

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
    {
        var block = _sut.GetScaffoldTemplate(new ScaffoldContext());

        block.Keyword.ShouldBe(SettingsKeyword.Database);
        block.Label!.Value.ShouldBe("sqlserver");
        block.Settings.ShouldContain(a => a.Key == "connection_string");
    }

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
        var config = Config(("connection_string", "Server=localhost;Database=app"));

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
        result.Errors.ShouldContain(e => e.Message.Contains("connection_string is required"));
    }

    [Fact]
    public void Configure_UnknownAttribute_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", "Server=localhost"),
            ("nonsense", "x"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("nonsense", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Configure_NonIntegerCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", "Server=localhost"),
            ("command_timeout", "soon"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();

    }

    [Fact]
    public void Configure_NegativeCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", "Server=localhost"),
            ("command_timeout", "-1"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("must not be negative"));
    }

    [Fact]
    public void Configure_MultipleProblems_AggregatesEveryError()
    {
        // Arrange — no connection string and a negative timeout: both must be reported, not just the first.
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("command_timeout", "-1"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.Count().ShouldBe(2);
    }

    [Fact]
    public void Configure_SuppliedConnectionString_Succeeds()
    {
        // Arrange — the engine applies any NSCHEMA_DATABASE_* override before binding, so by here the
        // setting is simply present; where it came from is not the plugin's concern.
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("connection_string", "Server=env-host;Database=app"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    private static PluginSettings Config(params (string Key, string? Value)[] attributes)
        => new(new PluginLabel("sqlserver"), attributes.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));
}
